using EmbyIcons.Api;
using MediaBrowser.Model.Services;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace EmbyIcons.Services
{
    [Route(ApiRoutes.GetApiRoutes, "GET", Summary = "Gets all API routes for the plugin")]
    public class GetApiRoutesRequest : IReturn<Dictionary<string, string>> { }

    public class ApiRoutesService : IService
    {
        public Task<object> Get(GetApiRoutesRequest request)
        {
            var routes = typeof(ApiRoutes)
                .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                .Where(fi => fi.IsLiteral && !fi.IsInitOnly && fi.FieldType == typeof(string))
                .ToDictionary(fi => fi.Name, fi => (string)fi.GetRawConstantValue());

            return Task.FromResult<object>(routes);
        }
    }
}