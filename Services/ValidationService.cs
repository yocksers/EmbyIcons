using EmbyIcons.Api;
using EmbyIcons.Caching;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Services;
using System.IO;
using System.Linq;

namespace EmbyIcons.Services
{
    [Route(ApiRoutes.ValidatePath, "GET", Summary = "Validates if a given path exists on the server")]
    public class ValidatePathRequest : IReturn<ValidatePathResponse>
    {
        [ApiMember(Name = "Path", Description = "The path to validate.", IsRequired = true, DataType = "string", ParameterType = "query")]
        public string Path { get; set; } = string.Empty;
    }

    public class ValidatePathResponse
    {
        public bool Exists { get; set; }
        public bool HasImages { get; set; }
    }

    public class ValidationService : IService
    {
        private readonly IFileSystem _fileSystem;

        public ValidationService(IFileSystem fileSystem)
        {
            _fileSystem = fileSystem;
        }

        public object Get(ValidatePathRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Path))
            {
                return new ValidatePathResponse { Exists = false, HasImages = false };
            }

            var path = System.Environment.ExpandEnvironmentVariables(request.Path);
            var exists = _fileSystem.DirectoryExists(path);
            var hasImages = false;

            if (exists)
            {
                hasImages = _fileSystem.GetFiles(path)
                    .Any(f =>
                    {
                        var ext = Path.GetExtension(f.FullName).ToLowerInvariant();
                        return Caching.IconCacheManager.SupportedCustomIconExtensions.Contains(ext);
                    });
            }

            return new ValidatePathResponse { Exists = exists, HasImages = hasImages };
        }
    }
}