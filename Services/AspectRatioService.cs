using EmbyIcons.Api;
using EmbyIcons.Helpers;
using MediaBrowser.Model.Services;
using System;

namespace EmbyIcons.Services
{
    [Route(ApiRoutes.AspectRatio, "GET", Summary = "Calculates aspect ratio information for given dimensions")]
    public class GetAspectRatio : IReturn<AspectRatioResponse>
    {
        [ApiMember(Name = "Width", Description = "The width of the video.", IsRequired = true, DataType = "int", ParameterType = "query")]
        public int Width { get; set; }

        [ApiMember(Name = "Height", Description = "The height of the video.", IsRequired = true, DataType = "int", ParameterType = "query")]
        public int Height { get; set; }
    }

    public class AspectRatioResponse
    {
        public double DecimalRatio { get; set; }
        public string SnappedName { get; set; } = string.Empty;
        public string PreciseName { get; set; } = string.Empty;
    }

    public class AspectRatioService : IService
    {
        private static ulong Gcd(ulong a, ulong b)
        {
            while (b != 0)
            {
                ulong temp = b;
                b = a % b;
                a = temp;
            }
            return a;
        }

        public object Get(GetAspectRatio request)
        {
            if (request.Width <= 0 || request.Height <= 0)
            {
                return new AspectRatioResponse();
            }

            var snappedName = MediaStreamHelper.GetAspectRatioIconName(request.Width, request.Height, snapToCommon: true);

            var divisor = Gcd((ulong)request.Width, (ulong)request.Height);

            var preciseName = $"{(ulong)request.Width / divisor}x{(ulong)request.Height / divisor}";

            return new AspectRatioResponse
            {
                DecimalRatio = (double)request.Width / request.Height,
                SnappedName = snappedName ?? "unknown",
                PreciseName = preciseName
            };
        }
    }
}