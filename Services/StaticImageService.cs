using EmbyIcons.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace EmbyIcons.Services
{
    [Authenticated]
    [Route(ApiRoutes.StaticImage, "GET", Summary = "Returns a static embedded image asset from the plugin")]
    public class GetStaticImage : IReturn<Stream>
    {
        public string Name { get; set; } = string.Empty;
    }

    public class StaticImageService : IService
    {
        public async Task<object> Get(GetStaticImage request)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return new MemoryStream();

            var name = Path.GetFileName(request.Name);
            if (string.IsNullOrWhiteSpace(name))
                return new MemoryStream();

            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = $"{typeof(Plugin).Namespace}.Images.{name}";

            var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                return new MemoryStream();

            var ms = new MemoryStream();
            await stream.CopyToAsync(ms).ConfigureAwait(false);
            stream.Dispose();
            ms.Position = 0;
            return ms;
        }
    }
}
