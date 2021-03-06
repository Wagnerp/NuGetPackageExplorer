﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using AuthenticodeExaminer;

using NuGet.Packaging;

using NuGetPackageExplorer.Types;

using NuGetPe;
using NuGetPe.AssemblyMetadata;

using PackageExplorerViewModel.Utilities;

namespace PackageExplorerViewModel
{
    public enum SymbolValidationResult
    {
        Valid,
        ValidExternal,
        InvalidSourceLink,
        NoSourceLink,
        NoSymbols,
        Pending,
        NothingToValidate
    }

#pragma warning disable CA1001 // Types that own disposable fields should be disposable
    public class SymbolValidator : INotifyPropertyChanged
#pragma warning restore CA1001 // Types that own disposable fields should be disposable
    {
        private readonly PackageViewModel _packageViewModel;
        private readonly IPackage _package;
        private readonly bool _publishedOnNuGetOrg;
        private readonly HttpClient _httpClient = new HttpClient();


        public SymbolValidator(PackageViewModel packageViewModel, IPackage package)
        {
            _packageViewModel = packageViewModel ?? throw new ArgumentNullException(nameof(packageViewModel));
            _package = package;
            _packageViewModel.PropertyChanged += _packageViewModel_PropertyChanged;

            Result = SymbolValidationResult.Pending;

            // NuGet signs all its packages and stamps on the service index. Look for that.
            if(package is ISignaturePackage sigPackage)
            {
                if (sigPackage.RepositorySignature?.V3ServiceIndexUrl?.AbsoluteUri.Contains(".nuget.org/", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _publishedOnNuGetOrg = true;
                }
            }            
        }

        private void _packageViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if(e.PropertyName == null)
            {
                Result = SymbolValidationResult.Pending;
                ErrorMessage = null;
                Refresh();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public async void Refresh()
        {
            try
            {
                // Get relevant files to check
                var libFiles = _packageViewModel.RootFolder["lib"]?.GetFiles() ?? Enumerable.Empty<IPackageFile>();
                var runtimeFiles = _packageViewModel.RootFolder["runtimes"]?.GetFiles() ?? Enumerable.Empty<IPackageFile>();
                var files = libFiles.Union(runtimeFiles).Where(pf => pf is PackageFile).Cast<PackageFile>().ToList();

                await Task.Run(async () => await CalculateValidity(files));
            }
            catch(Exception e)
            {
                DiagnosticsClient.TrackException(e);
            }
            finally
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Result)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorMessage)));
            }
        }

        private async Task CalculateValidity(IReadOnlyList<PackageFile> files)
        {
            var filesWithPdb = (from pf in files
                                let ext = Path.GetExtension(pf.Path)
                                where ".dll".Equals(ext, StringComparison.OrdinalIgnoreCase) ||
                                      ".exe".Equals(ext, StringComparison.OrdinalIgnoreCase) ||
                                      ".winmd".Equals(ext, StringComparison.OrdinalIgnoreCase)
                                select new FileWithPdb
                                {
                                    Primary = pf,
                                    Pdb = pf.GetAssociatedFiles().FirstOrDefault(af => ".pdb".Equals(Path.GetExtension(af.Path), StringComparison.OrdinalIgnoreCase))
                                })
                                .ToList();


            var sourceLinkErrors = new List<(PackageFile file, string errors)>();
            var noSourceLink = new List<PackageFile>();
            var noSymbols = new List<FileWithDebugData>();

            var allFilePaths = filesWithPdb.ToDictionary(pf => pf.Primary.Path);

            foreach(var file in filesWithPdb.ToArray()) // work on array as we'll remove items that are satellite assemblies as we go
            {
                // Skip satellite assemblies
                if(IsSatelliteAssembly(file.Primary.Path))
                {
                    filesWithPdb.Remove(allFilePaths[file.Primary.Path]);
                    continue;
                }

                // If we have a PDB, try loading that first. If not, may be embedded. 
                // Local checks first
                if(file.Pdb != null)
                {
                    ValidatePdb(new FileWithDebugData(file.Primary, null), file.Pdb.GetStream(), noSourceLink, sourceLinkErrors);                   
                }
                else // No PDB, see if it's embedded
                {
                    try
                    {

                        using var str = file.Primary.GetStream();
                        using var tempFile = new TemporaryFile(str);

                        var assemblyMetadata = AssemblyMetadataReader.ReadMetaData(tempFile.FileName);

                        if(assemblyMetadata?.DebugData.HasDebugInfo == true)
                        {
                            // we have an embedded pdb
                            if(!assemblyMetadata.DebugData.HasSourceLink)
                            {
                                noSourceLink.Add(file.Primary);
                            }

                            if (assemblyMetadata.DebugData.SourceLinkErrors.Count > 0)
                            {
                                // Has source link errors
                                sourceLinkErrors.Add((file.Primary, string.Join("\n", assemblyMetadata.DebugData.SourceLinkErrors)));
                            }
                        }
                        else // no embedded pdb, try to look for it elsewhere
                        {
                            noSymbols.Add(new FileWithDebugData(file.Primary, assemblyMetadata?.DebugData));
                        }

                    }
                    catch // an error occured, no symbols
                    {
                        noSymbols.Add(new FileWithDebugData(file.Primary, null));
                    }
                }
            }


            var requireExternal = false;
            // See if any pdb's are missing and check for a snupkg on NuGet.org. 
            if (noSymbols.Count > 0 && _publishedOnNuGetOrg)
            {
                // try to get on NuGet.org
                // https://www.nuget.org/api/v2/symbolpackage/Newtonsoft.Json/12.0.3 -- Will redirect               

                
                try
                {
#pragma warning disable CA2234 // Pass system uri objects instead of strings
                    var response = await _httpClient.GetAsync($"https://www.nuget.org/api/v2/symbolpackage/{_package.Id}/{_package.Version.ToNormalizedString()}").ConfigureAwait(false);
#pragma warning restore CA2234 // Pass system uri objects instead of strings

                    if (response.IsSuccessStatusCode) // we'll get a 404 if none
                    {
                        requireExternal = true;

                        using var getStream = await response.Content.ReadAsStreamAsync();
                        using var tempFile = new TemporaryFile(getStream, ".snupkg");
                        using var package = new ZipPackage(tempFile.FileName);

                        // Look for pdb's for the missing files
                        var dict = package.GetFiles().ToDictionary(k => k.Path);

                        foreach(var file in noSymbols.ToArray()) // from a copy so we can remove as we go
                        {
                            // file to look for                            
                            var pdbpath = Path.ChangeExtension(file.File.Path, ".pdb");

                            if(dict.TryGetValue(pdbpath, out var pdbfile))
                            {
                                noSymbols.Remove(file);

                                // Validate
                                ValidatePdb(file, pdbfile.GetStream(), noSourceLink, sourceLinkErrors);
                            }
                        }
                    }                    
                }
                catch // Could not check, leave status as-is
                {
                }
            }

            // Check for Microsoft assemblies on the Microsoft symbol server
            if (noSymbols.Count > 0)
            {
                var microsoftFiles = noSymbols.Where(f => f.DebugData != null && IsMicrosoftFile(f.File)).ToList();

                foreach(var file in microsoftFiles)
                {
                    var pdbStream = await GetSymbolsAsync(file.DebugData!.SymbolKeys);
                    if(pdbStream != null)
                    {
                        requireExternal = true;
                        noSymbols.Remove(file);
                        // Found a PDB for it
                        ValidatePdb(file, pdbStream, noSourceLink, sourceLinkErrors);
                    }
                }

            }

            if (noSymbols.Count == 0 && noSourceLink.Count == 0 && sourceLinkErrors.Count == 0)
            {
                if(filesWithPdb.Count == 0)
                {
                    Result = SymbolValidationResult.NothingToValidate;
                    ErrorMessage = "No files found to validate";
                }
                else if(requireExternal)
                {
                    Result = SymbolValidationResult.ValidExternal;
                    ErrorMessage = null;
                }
                else
                {
                    Result = SymbolValidationResult.Valid;
                    ErrorMessage = null;
                }
            }
            else
            {
                var found = false;
                var sb = new StringBuilder();
                if (noSourceLink.Count > 0)
                {                    
                    Result = SymbolValidationResult.NoSourceLink;

                    sb.AppendLine($"Missing Source Link for:\n{string.Join("\n", noSourceLink.Select(p => p.Path)) }");
                    found = true;
                }

                if(sourceLinkErrors.Count > 0)
                {
                    Result = SymbolValidationResult.InvalidSourceLink;

                    if (found)
                        sb.AppendLine();

                    foreach(var (file, errors) in sourceLinkErrors)
                    {
                        sb.AppendLine($"Source Link errors for {file.Path}:\n{string.Join("\n", errors) }");
                    }                    

                    found = true;
                }

                if (noSymbols.Count > 0) // No symbols "wins" as it's more severe
                {
                    Result = SymbolValidationResult.NoSymbols;

                    if (found)
                        sb.AppendLine();

                    sb.AppendLine($"Missing Symbols for:\n{string.Join("\n", noSymbols.Select(p => p.File.Path)) }");
                }

                ErrorMessage = sb.ToString();
            }
            
        }

        private static bool IsMicrosoftFile(PackageFile file)
        {
            IReadOnlyList<AuthenticodeSignature> sigs;
            SignatureCheckResult isValidSig;
            using (var str = file.GetStream())
            using (var tempFile = new TemporaryFile(str, Path.GetExtension(file.Name)))
            {
                var extractor = new FileInspector(tempFile.FileName);

                sigs = extractor.GetSignatures().ToList();
                isValidSig = extractor.Validate();

                if(isValidSig == SignatureCheckResult.Valid && sigs.Count > 0)
                {
                    return sigs[0].SigningCertificate.Subject.EndsWith(", O=Microsoft Corporation, L=Redmond, S=Washington, C=US", StringComparison.OrdinalIgnoreCase);
                }
            }

            return false;
        }

        private static void ValidatePdb(FileWithDebugData input, Stream pdbStream, List<PackageFile> noSourceLink, List<(PackageFile file, string errors)> sourceLinkErrors)
        {
            var peStream = MakeSeekable(input.File.GetStream(), true);
            try
            {
                // TODO: Verify that the PDB and DLL match

                // This might throw an exception because we don't know if it's a full PDB or portable
                // Try anyway in case it succeeds as a ppdb
                try
                {
                    if(input.DebugData == null || !input.DebugData.HasDebugInfo) // get it again if this is a shell with keys
                    {
                        using var stream = MakeSeekable(pdbStream, true);
                        input.DebugData = AssemblyMetadataReader.ReadDebugData(peStream, stream);
                    }


                    if (!input.DebugData.HasSourceLink)
                    {
                        // Have a PDB, but it's missing source link data
                        noSourceLink.Add(input.File);
                    }

                    if (input.DebugData.SourceLinkErrors.Count > 0)
                    {
                        // Has source link errors
                        sourceLinkErrors.Add((input.File, string.Join("\n", input.DebugData.SourceLinkErrors)));
                    }

                }
                catch (ArgumentNullException)
                {
                    // Have a PDB, but ithere's an error with the source link data
                    noSourceLink.Add(input.File);
                }
            }
            finally
            {
                peStream.Dispose();
            }
        }

        private static Stream MakeSeekable(Stream stream, bool disposeOriginal = false)
        {
            if (stream.CanSeek)
            {
                return stream;
            }

            var memoryStream = new MemoryStream();
            stream.CopyTo(memoryStream);
            memoryStream.Position = 0;

            if (disposeOriginal)
            {
                stream.Dispose();
            }
            return memoryStream;
        }


        // From https://github.com/ctaggart/SourceLink/blob/51e5b47ae64d87447a0803cec559947242fe935b/dotnet-sourcelink/Program.cs
        private static bool IsSatelliteAssembly(string path)
        {
            var match = Regex.Match(path, @"^(.*)\\[^\\]+\\([^\\]+).resources.dll$");

            return match.Success;

            // Satellite assemblies may not be in the same package as their main dll
           // return match.Success && dlls.Contains($"{match.Groups[1]}\\{match.Groups[2]}.dll");
        }


        private async Task<Stream?> GetSymbolsAsync(IReadOnlyList<SymbolKey> symbolKeys, CancellationToken cancellationToken = default)
        {
            foreach (var symbolKey in symbolKeys)
            {
               //var uri = new Uri(new Uri("https://symbols.nuget.org/download/symbols/"), symbolKey.Key);
               var uri = new Uri(new Uri("https://msdl.microsoft.com/download/symbols/"), symbolKey.Key);

                using var request = new HttpRequestMessage
                {
                    Method = HttpMethod.Get,
                    RequestUri = uri
                };

                if (symbolKey.Checksums?.Any() == true)
                {
                    request.Headers.Add("SymbolChecksum", string.Join(";", symbolKey.Checksums));
                }

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var pdbStream = new MemoryStream();
                await response.Content.CopyToAsync(pdbStream);
                pdbStream.Position = 0;

                return pdbStream;
            }

            return null;
        }

        public SymbolValidationResult Result
        {
            get; private set;
        }

        public string? ErrorMessage
        {
            get; private set;
        }

        private class FileWithPdb
        {
#pragma warning disable CS8618 // Non-nullable field is uninitialized. Consider declaring as nullable.
            public PackageFile Primary { get; set; }
#pragma warning restore CS8618 // Non-nullable field is uninitialized. Consider declaring as nullable.
            public PackageFile? Pdb { get; set; }
        }

        private class FileWithDebugData
        {
            public FileWithDebugData(PackageFile file, AssemblyDebugData? debugData)
            {
                File = file;
                DebugData = debugData;
            }

            public PackageFile File { get; }
            public AssemblyDebugData? DebugData { get; set; }
        }
    }
}
