using EmbyIcons.Api;
using EmbyIcons.Configuration;
using MediaBrowser.Model.Services;
using System.Threading.Tasks;

namespace EmbyIcons.Services
{
    [Route(ApiRoutes.DefaultProfile, "GET", Summary = "Gets a new icon profile with default settings")]
    public class GetDefaultProfile : IReturn<IconProfile> { }

    public class ProfileService : IService
    {
        public Task<object> Get(GetDefaultProfile request)
        {
            return Task.FromResult<object>(new IconProfile());
        }
    }
}