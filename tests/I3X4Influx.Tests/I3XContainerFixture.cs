using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace I3X4Influx.Tests;

/// <summary>
/// Builds the adapter's Docker image from the repository <c>Dockerfile</c> exactly once per test run.
///
/// Testcontainers stages the build context in a temp tar file named after the image, so two concurrent
/// builds of the same image collide on that file. Every fixture therefore goes through this gate rather
/// than calling <see cref="ImageFromDockerfileBuilder"/> itself.
/// </summary>
internal static class I3XImage
{
	private static readonly SemaphoreSlim Gate = new(1, 1);
	private static IFutureDockerImage? _image;

	public static async Task<IFutureDockerImage> GetAsync()
	{
		await Gate.WaitAsync().ConfigureAwait(false);

		try
		{
			if (_image == null)
			{
				IFutureDockerImage image = new ImageFromDockerfileBuilder()
					.WithDockerfileDirectory(CommonDirectoryPath.GetSolutionDirectory(), string.Empty)
					.WithDockerfile("Dockerfile")
					.WithName("i3x4influx:integrationtest")
					// Keep the image between runs so only changed layers are rebuilt. It is never disposed,
					// for the same reason - it is a local tag, not a running resource.
					.WithCleanUp(false)
					.Build();

				await image.CreateAsync().ConfigureAwait(false);
				_image = image;
			}

			return _image;
		}
		finally
		{
			Gate.Release();
		}
	}

	/// <summary>
	/// Readiness probe: <c>GET /v1/info</c> is the i3X health/capabilities endpoint and is exempt from
	/// authentication, so it answers 200 as soon as the app is listening.
	/// </summary>
	public static IWaitForContainerOS WaitStrategy() =>
		Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
			.ForPath("/v1/info")
			.ForPort(8080)
			.ForStatusCode(System.Net.HttpStatusCode.OK));
}

/// <summary>
/// Runs the adapter container with HTTP Basic authentication configured - the normal deployment shape.
///
/// No InfluxDB is required: <c>InfluxDataService.Connect()</c> logs and no-ops when INFLUX_TOKEN is
/// absent, and its queries then return empty result sets, so every endpoint still responds with a
/// well-formed (empty) payload. The tests therefore assert on behavior that is fully determined by the
/// container's configuration - authentication, CORS and the capability document - which keeps them
/// deterministic and independent of any live time-series data.
/// </summary>
public sealed class I3XContainerFixture : IAsyncLifetime
{
	public const string Username = "i3x-test";
	public const string Password = "test-password";

	private IContainer? _container;

	/// <summary>Base address of the running container, e.g. <c>http://localhost:32768</c>.</summary>
	public Uri BaseAddress { get; private set; } = null!;

	public async ValueTask InitializeAsync()
	{
		IFutureDockerImage image = await I3XImage.GetAsync().ConfigureAwait(false);

		_container = new ContainerBuilder(image)
			.WithEnvironment("ASPNETCORE_URLS", "http://+:8080")
			.WithEnvironment("I3X_BASIC_AUTH_USERNAME", Username)
			.WithEnvironment("I3X_BASIC_AUTH_PASSWORD", Password)
			// Deliberately no INFLUX_TOKEN: the adapter must start and serve regardless of whether a
			// backing InfluxDB is reachable.
			.WithPortBinding(8080, true)
			.WithWaitStrategy(I3XImage.WaitStrategy())
			.Build();

		await _container.StartAsync().ConfigureAwait(false);

		BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(8080)}");
	}

	public async ValueTask DisposeAsync()
	{
		if (_container != null)
		{
			await _container.DisposeAsync().ConfigureAwait(false);
		}
	}

	/// <summary>An <see cref="HttpClient"/> pointed at the container, without credentials.</summary>
	public HttpClient CreateClient() => new() { BaseAddress = BaseAddress };

	/// <summary>An <see cref="HttpClient"/> pointed at the container, pre-authenticated with HTTP Basic.</summary>
	public HttpClient CreateAuthenticatedClient()
	{
		HttpClient client = CreateClient();
		client.DefaultRequestHeaders.Authorization = BasicHeader(Username, Password);
		return client;
	}

	public static AuthenticationHeaderValue BasicHeader(string user, string password) =>
		new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
}

/// <summary>
/// Runs the adapter container with no authentication configured at all, to cover the fail-closed path.
/// It reuses the already-built image, so the extra cost is only a container start.
/// </summary>
public sealed class UnconfiguredAuthFixture : IAsyncLifetime
{
	private IContainer? _container;

	public Uri BaseAddress { get; private set; } = null!;

	public async ValueTask InitializeAsync()
	{
		IFutureDockerImage image = await I3XImage.GetAsync().ConfigureAwait(false);

		_container = new ContainerBuilder(image)
			.WithEnvironment("ASPNETCORE_URLS", "http://+:8080")
			// Deliberately no I3X_BASIC_AUTH_* and no I3X_OAUTH2_AUTHORITY.
			.WithPortBinding(8080, true)
			.WithWaitStrategy(I3XImage.WaitStrategy())
			.Build();

		await _container.StartAsync().ConfigureAwait(false);

		BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(8080)}");
	}

	public async ValueTask DisposeAsync()
	{
		if (_container != null)
		{
			await _container.DisposeAsync().ConfigureAwait(false);
		}
	}

	public HttpClient CreateClient() => new() { BaseAddress = BaseAddress };
}

/// <summary>
/// Every container test belongs to this collection. xUnit runs a collection's tests sequentially, which
/// keeps the Docker operations (image build, container starts) from racing each other.
/// </summary>
[CollectionDefinition(Name)]
public sealed class I3XContainerCollection : ICollectionFixture<I3XContainerFixture>
{
	public const string Name = "i3x-container";
}
