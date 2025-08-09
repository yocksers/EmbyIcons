using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons.Helpers
{
    internal static class FileUtils
    {
        public static async Task SafeCopyAsync(string inputFile, string outputFile, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? ".");

            var tempOutput = outputFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var fsIn = new FileStream(inputFile, FileMode.Open, FileAccess.Read, FileShare.Read, 262144, useAsync: true))
                await using (var fsOut = new FileStream(tempOutput, FileMode.Create, FileAccess.Write, FileShare.None, 262144, useAsync: true))
                {
                    await fsIn.CopyToAsync(fsOut, 262144, cancellationToken);
                }

                if (File.Exists(outputFile))
                {
                    try { File.Replace(tempOutput, outputFile, null); }
                    catch (System.IO.IOException)
                    {
                        try
                        {
                            if (File.Exists(outputFile)) File.Copy(tempOutput, outputFile, overwrite: true);
                            else File.Move(tempOutput, outputFile);
                            if (File.Exists(tempOutput)) File.Delete(tempOutput);
                        }
                        catch
                        {
                            throw;
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
                try { if (File.Exists(tempOutput)) File.Delete(tempOutput); } catch { }
                throw;
            }
        }
    }
}
