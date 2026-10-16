using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Pufferfin.Server.Arr.Models;
using Pufferfin.Server.Configuration;

namespace Pufferfin.Server.Arr;

/// <summary>
/// The v1 API of Prowlarr.
/// </summary>
public sealed class ProwlarrClient
{
    private readonly ArrHttpClient _http;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProwlarrClient"/> class.
    /// </summary>
    /// <param name="http">The HTTP client.</param>
    public ProwlarrClient(ArrHttpClient http)
    {
        _http = http;
    }

    /// <summary>Gets the items of a resource (<c>applications</c>, <c>indexerProxy</c>, <c>indexer</c>, <c>tag</c>...) as raw objects.</summary>
    /// <param name="service">The Prowlarr settings.</param>
    /// <param name="resource">The resource name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The items.</returns>
    public Task<List<JsonObject>> GetItemsAsync(ServiceSettings service, string resource, CancellationToken cancellationToken)
        => _http.GetAsync<List<JsonObject>>(service, "api/v1/" + resource, cancellationToken);

    /// <summary>Gets the provider templates of a resource.</summary>
    /// <param name="service">The Prowlarr settings.</param>
    /// <param name="resource">The resource name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The templates.</returns>
    public Task<List<JsonObject>> GetSchemaAsync(ServiceSettings service, string resource, CancellationToken cancellationToken)
        => _http.GetAsync<List<JsonObject>>(service, "api/v1/" + resource + "/schema", cancellationToken);

    /// <summary>Creates an item.</summary>
    /// <param name="service">The Prowlarr settings.</param>
    /// <param name="resource">The resource name.</param>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The created item.</returns>
    public Task<JsonObject> CreateItemAsync(ServiceSettings service, string resource, JsonObject item, CancellationToken cancellationToken)
        => _http.SendAsync<JsonObject>(service, HttpMethod.Post, "api/v1/" + resource, item, cancellationToken);

    /// <summary>Writes an item back.</summary>
    /// <param name="service">The Prowlarr settings.</param>
    /// <param name="resource">The resource name.</param>
    /// <param name="id">The item id.</param>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task UpdateItemAsync(ServiceSettings service, string resource, int id, JsonObject item, CancellationToken cancellationToken)
        => _http.SendAsync(service, HttpMethod.Put, "api/v1/" + resource + "/" + id.ToString(CultureInfo.InvariantCulture), item, cancellationToken);

    /// <summary>Tests an indexer. A failure comes back as an <see cref="ArrApiException"/> carrying the reason.</summary>
    /// <param name="service">The Prowlarr settings.</param>
    /// <param name="indexer">The indexer.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task TestIndexerAsync(ServiceSettings service, JsonObject indexer, CancellationToken cancellationToken)
        => _http.SendAsync(service, HttpMethod.Post, "api/v1/indexer/test", indexer, cancellationToken);

    /// <summary>
    /// Searches every indexer of Prowlarr by words. Slow: it waits on every indexer.
    /// </summary>
    /// <param name="service">The Prowlarr settings.</param>
    /// <param name="query">The words searched.</param>
    /// <param name="categories">The Newznab categories: 2000 for films, 5000 for series.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The releases found.</returns>
    public Task<List<ProwlarrRelease>> SearchAsync(ServiceSettings service, string query, int categories, CancellationToken cancellationToken)
        => _http.GetAsync<List<ProwlarrRelease>>(
            service,
            string.Create(CultureInfo.InvariantCulture, $"api/v1/search?query={System.Uri.EscapeDataString(query)}&type=search&categories={categories}&limit=100"),
            cancellationToken,
            isSearch: true);
}
