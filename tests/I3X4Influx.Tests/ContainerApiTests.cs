using System.Net;
using System.Text.Json;
using Xunit;

namespace I3X4Influx.Tests;

/// <summary>
/// End-to-end tests against the adapter running in its published Docker container.
///
/// They cover the contract that holds regardless of what is in InfluxDB: the container starts and stays
/// healthy without a backing store, the anonymous capability document is correct, authentication is
/// enforced on data endpoints, and CORS preflight is not challenged.
/// </summary>
[Collection(I3XContainerCollection.Name)]
public sealed class ContainerApiTests(I3XContainerFixture fixture)
{
	private static CancellationToken Token => TestContext.Current.CancellationToken;

	[Fact]
	public async Task Info_IsAnonymous_AndReportsI3XCapabilities()
	{
		using HttpClient client = fixture.CreateClient();

		using HttpResponseMessage response = await client.GetAsync("/v1/info", Token);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
		JsonElement result = document.RootElement.GetProperty("result");

		Assert.Equal("I3X4Influx", result.GetProperty("serverName").GetString());
		Assert.Equal("1.0", result.GetProperty("specVersion").GetString());

		JsonElement capabilities = result.GetProperty("capabilities");
		Assert.True(capabilities.GetProperty("query").GetProperty("history").GetBoolean());
		Assert.True(capabilities.GetProperty("subscribe").GetProperty("stream").GetBoolean());

		// Writes are not implemented by this adapter.
		JsonElement update = capabilities.GetProperty("update");
		Assert.False(update.GetProperty("current").GetBoolean());
		Assert.False(update.GetProperty("history").GetBoolean());
	}

	[Theory]
	[InlineData("/v1/objects")]
	[InlineData("/v1/namespaces")]
	[InlineData("/v1/objecttypes")]
	[InlineData("/v1/relationshiptypes")]
	public async Task DataEndpoints_WithoutCredentials_Challenge401(string path)
	{
		using HttpClient client = fixture.CreateClient();

		using HttpResponseMessage response = await client.GetAsync(path, Token);

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
		Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Basic");
	}

	[Fact]
	public async Task DataEndpoints_WithWrongPassword_Are401()
	{
		using HttpClient client = fixture.CreateClient();
		client.DefaultRequestHeaders.Authorization =
			I3XContainerFixture.BasicHeader(I3XContainerFixture.Username, "not-the-password");

		using HttpResponseMessage response = await client.GetAsync("/v1/objects", Token);

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Theory]
	[InlineData("/v1/objects")]
	[InlineData("/v1/namespaces")]
	[InlineData("/v1/objecttypes")]
	[InlineData("/v1/relationshiptypes")]
	public async Task DataEndpoints_WithValidCredentials_ReturnSuccessEnvelope(string path)
	{
		using HttpClient client = fixture.CreateAuthenticatedClient();

		using HttpResponseMessage response = await client.GetAsync(path, Token);

		// No InfluxDB is configured, so the collections come back empty - but the request must still be
		// authorized and shaped as the i3X success envelope rather than failing.
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
		Assert.True(document.RootElement.GetProperty("success").GetBoolean());
		Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("result").ValueKind);
	}

	[Fact]
	public async Task RelationshipTypes_ExposeWellKnownOpcUaReferenceTypes()
	{
		using HttpClient client = fixture.CreateAuthenticatedClient();

		using HttpResponseMessage response = await client.GetAsync("/v1/relationshiptypes", Token);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));

		// These are served from a static table, so they are present even without a backing store.
		var names = document.RootElement.GetProperty("result")
			.EnumerateArray()
			.Select(element => element.GetProperty("displayName").GetString())
			.ToList();

		Assert.Contains("HasComponent", names);
		Assert.Contains("Organizes", names);
	}

	[Fact]
	public async Task CorsPreflight_IsNotChallenged_AndAllowsTheOrigin()
	{
		using HttpClient client = fixture.CreateClient();

		using var request = new HttpRequestMessage(HttpMethod.Options, "/v1/objects");
		request.Headers.Add("Origin", "https://client.i3x.dev");
		request.Headers.Add("Access-Control-Request-Method", "GET");

		using HttpResponseMessage response = await client.SendAsync(request, Token);

		// Preflight must never be answered with 401, or the browser blocks the real request.
		Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
		Assert.Contains("Access-Control-Allow-Origin", response.Headers.Select(header => header.Key));
	}

	[Fact]
	public async Task SwaggerUi_IsServed_ForInteractiveTesting()
	{
		using HttpClient client = fixture.CreateClient();

		using HttpResponseMessage response = await client.GetAsync("/swagger/v1/swagger.json", Token);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Contains("I3X4Influx", await response.Content.ReadAsStringAsync(Token));
	}
}
