using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services.Updating;







public interface IUpdateSource
{
    
    string DisplayName { get; }

    
    bool IsConfigured { get; }

    
    
    
    
    Task<IReadOnlyList<UpdateRelease>> GetReleasesAsync(UpdateChannel channel, int max, CancellationToken ct = default);
}
