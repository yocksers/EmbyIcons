using MediaBrowser.Model.IO;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons.Helpers
{
    internal sealed class SourceImageUnavailableException : IOException
    {
        public SourceImageUnavailableException(string path, Exception innerException)
            : base($"The source image could not be read: {path} ({innerException.Message})", innerException)
        {
            SourcePath = path;
        }

        public string SourcePath { get; }
    }

    internal static class FileUtils
    {
        public static Stream OpenSourceImage(string inputFile, IFileSystem fileSystem)
        {
            try
            {
                return fileSystem.GetFileStream(inputFile, FileOpenMode.Open, FileAccessMode.Read, FileShareMode.Read, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new SourceImageUnavailableException(inputFile, ex);
            }
        }

        public static async Task SafeCopyAsync(string inputFile, string outputFile, IFileSystem fileSystem, CancellationToken cancellationToken)
        {
            using var fsIn = OpenSourceImage(inputFile, fileSystem);

            Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? ".");

            var tempOutput = outputFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var fsOut = new FileStream(tempOutput, FileMode.Create, FileAccess.Write, FileShare.None, 262144, useAsync: true))
                {
                    await fsIn.CopyToAsync(fsOut, 262144, cancellationToken);
                }

                if (File.Exists(outputFile))
                {
                    try { File.Replace(tempOutput, outputFile, null); }
                    catch (System.IO.IOException)
                    {
                        if (File.Exists(outputFile)) File.Copy(tempOutput, outputFile, overwrite: true);
                        else File.Move(tempOutput, outputFile);

                        try { File.Delete(tempOutput); }
                        catch (Exception cleanupEx)
                        {
                            Plugin.Instance?.Logger?.Warn($"[EmbyIcons] Failed to delete temp file '{tempOutput}': {cleanupEx.Message}");
                        }
                    }
                }
                else
                {
                    File.Move(tempOutput, outputFile);
                }
            }
            catch
            {
                try 
                { 
                    if (File.Exists(tempOutput)) 
                        File.Delete(tempOutput); 
                } 
                catch (Exception cleanupEx) 
                { 
                    Plugin.Instance?.Logger?.Warn($"[EmbyIcons] Failed to clean up temp file '{tempOutput}': {cleanupEx.Message}");
                }
                throw;
            }
        }
    }
}