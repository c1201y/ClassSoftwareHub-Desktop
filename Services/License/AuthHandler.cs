using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.License;
















public sealed class AuthHandler : DelegatingHandler
{
    
    public AuthHandler() : this(new HttpClientHandler()) { }

    public AuthHandler(HttpMessageHandler inner) : base(inner) { }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ClientAuth.Apply(request);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        ClientAuth.Capture(response);

        
        
        
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            try
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                response.Content = new StringContent(text, Encoding.UTF8, "application/json");

                if (ClientAuth.HasLicenseCode(text)) ClientAuth.RaiseActivationRequired();
            }
            catch
            {
                
            }
        }

        return response;
    }
}
