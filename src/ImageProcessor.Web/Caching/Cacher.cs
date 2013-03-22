﻿

namespace ImageProcessor.Web.Caching
{
    #region Using
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using System.Web;
    using System.Web.Hosting;
    using ImageProcessor.Helpers.Extensions;
    using ImageProcessor.Web.Config;
    #endregion

    /// <summary>
    /// The cache.
    /// </summary>
    internal sealed class Cache
    {
        #region Fields

        /// <summary>
        /// The maximum number of days to cache files on the system for.
        /// </summary>
        internal static readonly int MaxFileCachedDuration = ImageProcessorConfig.Instance.MaxCacheDays;

        /// <summary>
        /// The valid sub directory chars. This used in combination with the file limit per folder
        /// allows the storage of 360,000 image files in the cache.
        /// </summary>
        private const string ValidSubDirectoryChars = "abcdefghijklmnopqrstuvwxyz0123456789";

        /// <summary>
        /// The maximum number of files allowed in the directory.
        /// </summary>
        /// <remarks>
        /// NTFS directories can handle up to 10,000 files in the directory before slowing down. 
        /// This will help us to ensure that don't go over that limit.
        /// <see cref="http://stackoverflow.com/questions/197162/ntfs-performance-and-large-volumes-of-files-and-directories"/>
        /// <see cref="http://stackoverflow.com/questions/115882/how-do-you-deal-with-lots-of-small-files"/>
        /// <see cref="http://stackoverflow.com/questions/1638219/millions-of-small-graphics-files-and-how-to-overcome-slow-file-system-access-on"/>
        /// </remarks>
        private const int MaxFilesCount = 10000;

        /// <summary>
        /// The regular expression to search strings for file extensions.
        /// </summary>
        private static readonly Regex FormatRegex = new Regex(
            @"(jpeg|png|bmp|gif)", RegexOptions.RightToLeft | RegexOptions.Compiled);

        /// <summary>
        /// The regular expression to search strings for valid subfolder names.
        /// We're specifically not using a shorter regex as we need to be able to iterate through
        /// each match group.
        /// </summary>
        private static readonly Regex SubFolderRegex =
            new Regex(
                @"(\/(a|b|c|d|e|f|g|h|i|j|k|l|m|n|o|p|q|r|s|t|u|v|w|x|y|z|0|1|2|3|4|5|6|7|8|9)\/)",
                RegexOptions.Compiled);

        /// <summary>
        /// The absolute path to virtual cache path on the server.
        /// </summary>
        private static readonly string AbsoluteCachePath =
            HostingEnvironment.MapPath(ImageProcessorConfig.Instance.VirtualCachePath);

        /// <summary>
        /// The concurrent dictionary.
        /// </summary>
        private ConcurrentDictionary<string, CachedImage> concurrentDictionary =
            new ConcurrentDictionary<string, CachedImage>();

        #endregion

        #region Methods

        public void Test()
        {
            Task<bool> task = this.CreateDirectoriesAsync();
            if (task.Result)
            {

            }

        }

        #region Internal
        /// <summary>
        /// Converts an absolute file path 
        /// </summary>
        /// <param name="absolutePath">The absolute path to convert.</param>
        /// <param name="request">The <see cref="T:System.Web.HttpRequest"/>from the current context.</param>
        /// <returns>The virtual path to the file.</returns>
        internal string GetVirtualPath(string absolutePath, HttpRequest request)
        {
            string applicationPath = request.PhysicalApplicationPath;
            string virtualDir = request.ApplicationPath;
            virtualDir = virtualDir == "/" ? virtualDir : (virtualDir + "/");

            if (applicationPath != null)
            {
                return absolutePath.Replace(applicationPath, virtualDir).Replace(@"\", "/");
            }

            throw new InvalidOperationException(
                "We can only map an absolute back to a relative path if the application path is available.");
        }

        /// <summary>
        /// Gets the full transformed cached path for the image.
        /// The file names are stored as MD5 encrypted versions of the full request path.
        /// This should make them unique enough to 
        /// </summary>
        /// <param name="imagePath">The original image path.</param>
        /// <param name="imageName">The original image name.</param>
        /// <returns>The full cached path for the image.</returns>
        internal string GetCachePath(string imagePath, string imageName)
        {
            string cachedPath = string.Empty;

            if (AbsoluteCachePath != null)
            {
                // Use an md5 hash of the full path including the querystring to create the image name. 
                // That name can also be used as a key for the cached image and we should be able to use 
                // The first character of that hash as a subfolder.
                string parsedExtension = this.ParseExtension(imagePath);
                string fallbackExtension = imageName.Substring(imageName.LastIndexOf(".", StringComparison.Ordinal) + 1);
                string encryptedName = imagePath.ToMD5Fingerprint();
                string subpath = encryptedName.Substring(0, 1);

                string cachedFileName = string.Format(
                    "{0}.{1}",
                    encryptedName,
                    !string.IsNullOrWhiteSpace(parsedExtension) ? parsedExtension : fallbackExtension);

                cachedPath = Path.Combine(AbsoluteCachePath, subpath, cachedFileName);
            }

            return cachedPath;
        }

        /// <summary>
        /// Creates the cache directories for storing images.
        /// </summary>
        /// <returns>
        /// The true if the cache directories are created successfully; otherwise, false.
        /// </returns>
        internal /*async*/ Task<bool> CreateDirectoriesAsync()
        {
            return this.CreateDirectoriesAsyncTasks().ToTask<bool>();
        }

        /// <summary>
        /// Adds an image to the cache.
        /// </summary>
        /// <param name="cachedPath">
        /// The cached path.
        /// </param>
        /// <param name="lastWriteTimeUtc">
        /// The last write time.
        /// </param>
        /// <returns>
        /// The task.
        /// </returns>
        internal Task /*async*/ AddImageToCacheAsync(string cachedPath, DateTime lastWriteTimeUtc)
        {
            return this.AddImageToCacheAsyncTask(cachedPath, lastWriteTimeUtc).ToTask();
        }

        /// <summary>
        /// Returns a value indicating whether the original file has been updated.
        /// </summary>
        /// <param name="imagePath">The original image path.</param>
        /// <param name="cachedImagePath">The cached image path.</param>
        /// <param name="isRemote">Whether the file is a remote request.</param>
        /// <returns>
        /// True if the the original file has been updated; otherwise, false.
        /// </returns>
        internal /*async*/ Task<bool> IsUpdatedFileAsync(string imagePath, string cachedImagePath, bool isRemote)
        {
            return this.IsUpdatedFileAsyncTask(imagePath, cachedImagePath, isRemote).ToTask<bool>();
        }

        /// <summary>
        /// Sets the LastWriteTime of the cached file to match the original file.
        /// </summary>
        /// <param name="imagePath">
        /// The original image path.
        /// </param>
        /// <param name="cachedImagePath">
        /// The cached image path.
        /// </param>
        /// <param name="isRemote">Whether the file is remote.</param>
        /// <returns>
        /// The <see cref="System.DateTime"/> set to the last write time of the file.
        /// </returns>
        internal /*async*/ Task<DateTime> SetCachedLastWriteTimeAsync(string imagePath, string cachedImagePath, bool isRemote)
        {
            return this.SetCachedLastWriteTimeAsyncTask(imagePath, cachedImagePath, isRemote).ToTask<DateTime>();
        }

        #endregion

        #region Private
        /// <summary>
        /// The create directories async tasks.
        /// </summary>
        /// <returns>
        /// The <see cref="IEnumerable{Task}"/>.
        /// </returns>
        private IEnumerable<Task> CreateDirectoriesAsyncTasks()
        {
            bool success = true;

            try
            {
                Parallel.ForEach(
                    ValidSubDirectoryChars.ToCharArray(),
                    (extension, loop) =>
                    {
                        string path = Path.Combine(AbsoluteCachePath, extension.ToString(CultureInfo.InvariantCulture));
                        DirectoryInfo directoryInfo = new DirectoryInfo(path);

                        if (!directoryInfo.Exists)
                        {
                            directoryInfo.Create();
                        }
                    });
            }
            catch
            {
                success = false;
            }

            yield return TaskEx.FromResult(success);
        }

        /// <summary>
        /// Adds an image to the cache.
        /// </summary>
        /// <param name="cachedPath">
        /// The cached path.
        /// </param>
        /// <param name="lastWriteTimeUtc">
        /// The last write time.
        /// </param>
        /// <returns>
        /// The <see cref="IEnumerable{Task}"/>.
        /// </returns>
        private IEnumerable<Task> AddImageToCacheAsyncTask(string cachedPath, DateTime lastWriteTimeUtc)
        {
            string key = Path.GetFileNameWithoutExtension(cachedPath);
            DateTime expires = DateTime.UtcNow.AddDays(MaxFileCachedDuration).ToUniversalTime();
            CachedImage cachedImage = new CachedImage(cachedPath, MaxFileCachedDuration, lastWriteTimeUtc, expires);
            PersistantDictionary.Instance.Add(key, cachedImage);

            yield break;
        }

        /// <summary>
        /// Returns a value indicating whether the original file has been updated.
        /// </summary>
        /// <param name="imagePath">The original image path.</param>
        /// <param name="cachedImagePath">The cached image path.</param>
        /// <param name="isRemote">Whether the file is a remote request.</param>
        /// <returns>
        /// The <see cref="IEnumerable{Task}"/>.
        /// </returns>
        private IEnumerable<Task> IsUpdatedFileAsyncTask(string imagePath, string cachedImagePath, bool isRemote)
        {
            string key = Path.GetFileNameWithoutExtension(cachedImagePath);
            CachedImage cachedImage;
            bool isUpdated = false;

            if (isRemote)
            {
                if (PersistantDictionary.Instance.TryGetValue(key, out cachedImage))
                {
                    // Can't check the last write time so check to see if the cached image is set to expire 
                    // or if the max age is different.
                    if (cachedImage.ExpiresUtc < DateTime.UtcNow.AddDays(-MaxFileCachedDuration)
                        || cachedImage.MaxAge != MaxFileCachedDuration)
                    {
                        if (PersistantDictionary.Instance.TryRemove(key, out cachedImage))
                        {
                            isUpdated = true;
                        }
                    }
                }
                else
                {
                    // Nothing in the cache so we should return true.
                    isUpdated = true;
                }
            }

            // Test now for locally requested files.
            if (PersistantDictionary.Instance.TryGetValue(key, out cachedImage))
            {
                FileInfo imageFileInfo = new FileInfo(imagePath);

                if (imageFileInfo.Exists)
                {
                    // Check to see if the last write time is different of whether the
                    // cached image is set to expire or if the max age is different.
                    if (imageFileInfo.LastWriteTimeUtc != cachedImage.LastWriteTimeUtc
                        || cachedImage.ExpiresUtc < DateTime.UtcNow.AddDays(-MaxFileCachedDuration)
                        || cachedImage.MaxAge != MaxFileCachedDuration)
                    {
                        if (PersistantDictionary.Instance.TryRemove(key, out cachedImage))
                        {
                            isUpdated = true;
                        }
                    }
                }
            }
            else
            {
                // Nothing in the cache so we should return true.
                isUpdated = true;
            }

            yield return TaskEx.FromResult(isUpdated);
        }

        /// <summary>
        /// Sets the LastWriteTime of the cached file to match the original file.
        /// </summary>
        /// <param name="imagePath">
        /// The original image path.
        /// </param>
        /// <param name="cachedImagePath">
        /// The cached image path.
        /// </param>
        /// <param name="isRemote">Whether the file is remote.</param>
        /// <returns>
        /// The <see cref="IEnumerable{Task}"/>.
        /// </returns>
        private IEnumerable<Task> SetCachedLastWriteTimeAsyncTask(string imagePath, string cachedImagePath, bool isRemote)
        {
            FileInfo cachedFileInfo = new FileInfo(cachedImagePath);
            DateTime lastWriteTime = DateTime.MinValue.ToUniversalTime();

            if (isRemote)
            {
                if (cachedFileInfo.Exists)
                {
                    lastWriteTime = cachedFileInfo.LastWriteTimeUtc;
                }
            }
            else
            {
                FileInfo imageFileInfo = new FileInfo(imagePath);

                if (imageFileInfo.Exists && cachedFileInfo.Exists)
                {
                    DateTime dateTime = imageFileInfo.LastWriteTimeUtc;
                    cachedFileInfo.LastWriteTimeUtc = dateTime;

                    lastWriteTime = dateTime;
                }
            }

            yield return TaskEx.FromResult(lastWriteTime);
        }

        /// <summary>
        /// Returns the correct file extension for the given string input
        /// </summary>
        /// <param name="input">
        /// The string to parse.
        /// </param>
        /// <returns>
        /// The correct file extension for the given string input if it can find one; otherwise an empty string.
        /// </returns>
        private string ParseExtension(string input)
        {
            Match match = FormatRegex.Match(input);

            return match.Success ? match.Value : string.Empty;
        }
        #endregion
        #endregion
    }
}
