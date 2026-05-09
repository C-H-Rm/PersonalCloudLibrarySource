using System;
using System.IO;

namespace PersonalCloudLibrarySource
{
    public class LocalFileCopier
    {
        public LocalCopyResult CopyFileToLocalPath(string sourceFilePath, string localFullFilePath)
        {
            if (string.IsNullOrWhiteSpace(sourceFilePath))
            {
                return LocalCopyResult.Fail("Source file path is empty.");
            }

            if (string.IsNullOrWhiteSpace(localFullFilePath))
            {
                return LocalCopyResult.Fail("Local destination file path could not be resolved.");
            }

            if (!File.Exists(sourceFilePath))
            {
                return LocalCopyResult.Fail($"Source file does not exist: {sourceFilePath}");
            }

            try
            {
                var destinationFolder = Path.GetDirectoryName(localFullFilePath);
                if (string.IsNullOrWhiteSpace(destinationFolder))
                {
                    return LocalCopyResult.Fail("Local destination folder could not be resolved.");
                }

                Directory.CreateDirectory(destinationFolder);
                File.Copy(sourceFilePath, localFullFilePath, true);
                return LocalCopyResult.Success("Local file copy completed.");
            }
            catch (Exception ex)
            {
                return LocalCopyResult.Fail("Local file copy failed.", ex);
            }
        }
    }

    public class LocalCopyResult
    {
        public bool Succeeded { get; private set; }
        public string Message { get; private set; }
        public Exception Exception { get; private set; }

        public static LocalCopyResult Success(string message)
        {
            return new LocalCopyResult
            {
                Succeeded = true,
                Message = message
            };
        }

        public static LocalCopyResult Fail(string message, Exception exception = null)
        {
            return new LocalCopyResult
            {
                Succeeded = false,
                Message = message,
                Exception = exception
            };
        }
    }
}
