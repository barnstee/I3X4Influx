using System.Net;
using Xunit;

namespace I3X4Influx.Tests;

/// <summary>
/// Verifies the documented fail-closed behavior: when neither HTTP Basic nor OAuth2 is configured, the
/// container must refuse to serve data endpoints with HTTP 503 rather than exposing them anonymously.
///
/// Uses <see cref="UnconfiguredAuthFixture"/> as a class fixture so the container is started once for the
/// whole class, and joins the container collection so it never runs alongside the other container tests.
/// </summary>
[Collection(I3XContainerCollection.Name)]
public sealed class UnconfiguredAuthContainerTests(UnconfiguredAuthFixture fixture)
	: IClassFixture<UnconfiguredAuthFixture>
{
	private static CancellationToken Token => TestContext.Current.CancellationToken;

	[Fact]
	public async Task DataEndpoints_FailClosed_With503()
	{
		using HttpClient client = fixture.CreateClient();

		using HttpResponseMessage response = await client.GetAsync("/v1/objects", Token);

		Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
	}

	[Fact]
	public async Task Info_RemainsAvailable_SoHealthChecksStillWork()
	{
		using HttpClient client = fixture.CreateClient();

		using HttpResponseMessage response = await client.GetAsync("/v1/info", Token);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}
}
