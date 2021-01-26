﻿using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using API.Extensions;
using API.Interfaces;
using Microsoft.Extensions.Logging;
using NetVips;

namespace API.Services
{
    /// <summary>
    /// Responsible for manipulating Archive files. Used by <see cref="CacheService"/> almost exclusively.
    /// </summary>
    public class ArchiveService : IArchiveService
    {
        private readonly ILogger<ArchiveService> _logger;

        public ArchiveService(ILogger<ArchiveService> logger)
        {
            _logger = logger;
        }
        
        public int GetNumberOfPagesFromArchive(string archivePath)
        {
            if (!File.Exists(archivePath) || !Parser.Parser.IsArchive(archivePath))
            {
                _logger.LogError($"Archive {archivePath} could not be found.");
                return 0;
            }
           
            _logger.LogDebug($"Getting Page numbers from  {archivePath}");

            try
            {
                using ZipArchive archive = ZipFile.OpenRead(archivePath); // ZIPFILE
                return archive.Entries.Count(e => Parser.Parser.IsImage(e.FullName));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "There was an exception when reading archive stream.");
                return 0;
            }
           
           
        }
        
        /// <summary>
        /// Generates byte array of cover image.
        /// Given a path to a compressed file (zip, rar, cbz, cbr, etc), will ensure the first image is returned unless
        /// a folder.extension exists in the root directory of the compressed file.
        /// </summary>
        /// <param name="filepath"></param>
        /// <param name="createThumbnail">Create a smaller variant of file extracted from archive. Archive images are usually 1MB each.</param>
        /// <returns></returns>
        public byte[] GetCoverImage(string filepath, bool createThumbnail = false)
        {
            try
            {
                if (string.IsNullOrEmpty(filepath) || !File.Exists(filepath) || !Parser.Parser.IsArchive(filepath)) return Array.Empty<byte>();

                _logger.LogDebug($"Extracting Cover image from {filepath}");
                using ZipArchive archive = ZipFile.OpenRead(filepath);
                if (!archive.HasFiles()) return Array.Empty<byte>();

                var folder = archive.Entries.SingleOrDefault(x => Path.GetFileNameWithoutExtension(x.Name).ToLower() == "folder");
                var entries = archive.Entries.Where(x => Path.HasExtension(x.FullName) && Parser.Parser.IsImage(x.FullName)).OrderBy(x => x.FullName).ToList();
                ZipArchiveEntry entry;
                
                if (folder != null)
                {
                    entry = folder;
                } else if (!entries.Any())
                {
                    return Array.Empty<byte>();
                }
                else
                {
                    entry = entries[0];
                }


                if (createThumbnail)
                {
                    try
                    {
                        using var stream = entry.Open();
                        var thumbnail = Image.ThumbnailStream(stream, 320);
                        return thumbnail.WriteToBuffer(".jpg");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "There was a critical error and prevented thumbnail generation.");
                    }
                }
                
                return ExtractEntryToImage(entry);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "There was an exception when reading archive stream.");
                return Array.Empty<byte>();
            }
        }
        
        private static byte[] ExtractEntryToImage(ZipArchiveEntry entry)
        {
            using var stream = entry.Open();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var data = ms.ToArray();

            return data;
        }

        /// <summary>
        /// Given an archive stream, will assess whether directory needs to be flattened so that the extracted archive files are directly
        /// under extract path and not nested in subfolders. See <see cref="DirectoryInfoExtensions"/> Flatten method.
        /// </summary>
        /// <param name="archive">An opened archive stream</param>
        /// <returns></returns>
        public bool ArchiveNeedsFlattening(ZipArchive archive)
        {
            // Sometimes ZipArchive will list the directory and others it will just keep it in the FullName
            return archive.Entries.Count > 0 &&
                !Path.HasExtension(archive.Entries.ElementAt(0).FullName) ||
                archive.Entries.Any(e => e.FullName.Contains(Path.AltDirectorySeparatorChar));
        }

        /// <summary>
        /// Extracts an archive to a temp cache directory. Returns path to new directory. If temp cache directory already exists,
        /// will return that without performing an extraction. Returns empty string if there are any invalidations which would
        /// prevent operations to perform correctly (missing archivePath file, empty archive, etc).
        /// </summary>
        /// <param name="archivePath">A valid file to an archive file.</param>
        /// <param name="extractPath">Path to extract to</param>
        /// <returns></returns>
        public void ExtractArchive(string archivePath, string extractPath)
        {
            if (!File.Exists(archivePath) || !Parser.Parser.IsArchive(archivePath))
            {
                _logger.LogError($"Archive {archivePath} could not be found.");
                return;
            }

            if (Directory.Exists(extractPath))
            {
                _logger.LogDebug($"Archive {archivePath} has already been extracted. Returning existing folder.");
                return;
            }
           
            Stopwatch sw = Stopwatch.StartNew();
            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            var needsFlattening = ArchiveNeedsFlattening(archive);
            if (!archive.HasFiles() && !needsFlattening) return;
            
            archive.ExtractToDirectory(extractPath);
            _logger.LogDebug($"Extracted archive to {extractPath} in {sw.ElapsedMilliseconds} milliseconds.");

            if (needsFlattening)
            {
                sw = Stopwatch.StartNew();
                _logger.LogInformation("Extracted archive is nested in root folder, flattening...");
                new DirectoryInfo(extractPath).Flatten();
                _logger.LogInformation($"Flattened in {sw.ElapsedMilliseconds} milliseconds");
            }
        }
    }
}