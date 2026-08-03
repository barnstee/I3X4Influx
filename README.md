# I3X4Influx
I3X API wrapper for InfluxDB.

## Supported i3X 1.0 capabilities
This adapter implements the read/query and subscription surface of the [i3X 1.0 API](https://api.i3x.dev/v1/docs). The `GET /v1/info` endpoint advertises the exact capabilities:

- **Query** (`GET /objects`, `POST /objects/list|related|value|history`, `GET/POST` object & relationship types, `GET /namespaces`) - including historical value queries (`query.history = true`).
- **Subscriptions** (`POST /subscriptions`, `.../register`, `.../unregister`, `.../list`, `.../delete`, `.../sync`, `.../stream`) - the client creates a subscription, registers element ids, and then either polls `sync` or opens an SSE `stream`. Because InfluxDB has no native change feed, both poll the telemetry measurement for values newer than each element's last-delivered timestamp (`subscribe.stream = true`). Subscription state is held in memory, so run a single replica.
- **Related-object queries** honor the `relationshipType` and its direction: forward containment types (`HasComponent`, `HasOrderedComponent`, `Organizes`, `HasProperty`) return an object's ISA-95 children; their reverses (`ComponentOf`, `OrganizedBy`, ...) return its parent. Relationships are derived from the OPC UA metadata's ISA-95 hierarchy (`Enterprise > Site > Area > Line > Workcell`), falling back to a namespace-grouped tree when those levels are absent.
- **Writes** (`PUT /objects/value|history`) are **not** implemented (`update.current = update.history = false`).

## InfluxDB schema
The adapter expects OPC UA PubSub payloads ingested into InfluxDB (for example by Telegraf from an MQTT broker fed by UA Cloud Publisher). It maps them onto the i3X model as follows:

| i3X / OPC UA concept | InfluxDB |
| --- | --- |
| Asset (dataset writer) | `datasetWriterId` tag on the telemetry measurement |
| Variable | Influx field key (`_field`) |
| Value / timestamp | `_value` / `_time` |
| DataSetName (carries the namespace URI) | `metaName` tag on the metadata measurement |
| ISA-95 levels | optional `Enterprise` / `Site` / `Area` / `Line` / `Workcell` tags on the metadata measurement (lower-case names are also accepted) |

The namespace URI is parsed out of the DataSetName, which is stored as a full ExpandedNodeId (`urn:<applicationUri>;nsu=<namespaceUri>;s=<identifier>`). When no ISA-95 tags are present, assets are grouped under a synthetic namespace container instead of an `Enterprise > ... > Workcell` path.

Browsing uses the InfluxDB `schema` package (`schema.tagValues`), which is answered from the index rather than by scanning series data, and all reads are bounded by an explicit `range()` so a query can never degrade into a full-bucket scan.

## Mandatory Environment Variables
* "INFLUX_TOKEN": the InfluxDB API token. Required; without it no queries are executed and the API returns empty results.
* "I3X_BASIC_AUTH_USERNAME" / "I3X_BASIC_AUTH_PASSWORD": HTTP Basic authentication credentials. At least one authentication method (HTTP Basic and/or OAuth2) must be configured; see [Security](#security). If neither method is configured, the API fails closed and returns HTTP 503.

## Optional Environment Variables
* "INFLUX_URL": the InfluxDB endpoint. Defaults to `http://localhost:8086`.
* "INFLUX_ORG": the InfluxDB organization. Defaults to `iot`.
* "INFLUX_BUCKET": the bucket holding the OPC UA telemetry and metadata. Defaults to `mqtt`.
* "INFLUX_MEASUREMENT": the telemetry measurement. Defaults to `opcua_pubsub`.
* "INFLUX_METADATA_MEASUREMENT": the OPC UA DataSet metadata measurement. Defaults to `opcua_metadata`.
* "INFLUX_TIMEOUT_SECONDS": the HTTP timeout (in seconds) for InfluxDB queries. The client default of 10 seconds is regularly exceeded by browse-style queries that scan many days of data (surfacing as a `TaskCanceledException`), so this defaults to 120.
* "INFLUX_BROWSE_RANGE": the lookback window used to discover the available assets and variables. Shorter windows are significantly faster; anything that has not reported within the window is not listed. Defaults to `-24h`.
* "INFLUX_LATEST_RANGE": the lookback window for current-value queries. Defaults to `-1h`.
* "I3X_CORS_ORIGINS": comma-separated list of allowed CORS origins. When unset, all origins are allowed (required so the browser-based CESMII i3X client can call the API cross-origin). Set this to lock CORS down to specific origins in production.
* "I3X_STREAM_POLL_MS": the SSE stream / sync poll interval against InfluxDB, in milliseconds. Defaults to 2000; values below 250 (or values that are not a number) fall back to that default.
* "I3X_METADATA_CACHE_SECONDS": how long the ISA-95 object/namespace/type metadata is cached in memory. Building it costs several Flux round trips and backs nearly every `/objects`, `/objecttypes` and `/namespaces` request, while OPC UA metadata changes rarely, so caching it sharply reduces the query volume against InfluxDB. Default 60, set to 0 to disable caching.
* "I3X_OAUTH2_AUTHORITY": the OpenID Connect authority (issuer) base URL. Setting this enables OAuth2 bearer-token authentication. See [Security](#security).
* "I3X_OAUTH2_AUDIENCE": expected audience (`aud`) claim(s) for OAuth2 access tokens; comma-separated for multiple values. When unset, the audience is not validated.
* "I3X_OAUTH2_ISSUER": expected token issuer. When unset, the issuer advertised by the authority's OIDC metadata is used.
* "ASPNETCORE_URLS": the standard ASP.NET Core hosting variable that selects the listening endpoints. The container image exposes ports 8080 (HTTP) and 8081 (HTTPS).

## Docker

The adapter ships with a multi-stage `Dockerfile` (build on the .NET SDK image, run on the ASP.NET runtime image) that listens on port 8080 (HTTP) and 8081 (HTTPS).

```bash
docker build -t i3x4influx:local .

docker run --rm -p 8080:8080 \
  -e ASPNETCORE_URLS="http://+:8080" \
  -e INFLUX_URL="http://<influx-host>:8086" \
  -e INFLUX_TOKEN="<influxdb-api-token>" \
  -e INFLUX_ORG="iot" \
  -e INFLUX_BUCKET="mqtt" \
  -e I3X_BASIC_AUTH_USERNAME="i3x" \
  -e I3X_BASIC_AUTH_PASSWORD="<strong-password>" \
  i3x4influx:local
```

Note that `INFLUX_URL` must be reachable from inside the container, so use the host's LAN address (or a Docker network alias) rather than `localhost`.

Pushes to `main` build and publish a multi-architecture (`linux/amd64`, `linux/arm64`) image to GitHub Container Registry via `.github/workflows/docker-publish.yml`.

## Testing the container

Open `I3X4Influx.sln` in Visual Studio (the solution, not just the folder - Test Explorer and the container tooling both need it).

### Interactively

1. Select the **Container (Dockerfile)** launch profile and press F5. Visual Studio builds the image, starts the container and opens the Swagger UI.
2. Use the Swagger UI, or open `I3X4Influx.http` and click **Send request** on any of the prepared calls. Set `@port` in that file to the host port Visual Studio mapped (visible in the **Containers** window under **Ports**).
3. The launch profiles set `I3X_BASIC_AUTH_USERNAME=i3x` / `I3X_BASIC_AUTH_PASSWORD=changeit`; the `@auth` variable in the `.http` file is the base64 of those credentials. Authentication is mandatory, so without them every data endpoint returns HTTP 503.

### Automatically

`tests/I3X4Influx.Tests` builds the image from this repository's `Dockerfile` with [Testcontainers](https://dotnet.testcontainers.org/), runs it, and asserts against the live container over HTTP. Run them from Test Explorer or with:

```bash
dotnet test
```

The tests need Docker running but **no InfluxDB**: the adapter starts and serves regardless of whether a backing store is reachable, so the suite covers the parts of the contract that do not depend on time-series data - the anonymous `/v1/info` capability document, the 401 challenge and 503 fail-closed behavior of `AuthMiddleware`, the CORS preflight exemption, the static relationship types and the generated OpenAPI document. The image is built once per run and shared by all tests, so a full run takes about ten seconds after the first build.

## Security
Authentication is **mandatory** and **cannot be turned off**. Two authentication methods are supported, and a request is accepted if it satisfies **either** one:

1. **HTTP Basic** - the client sends an `Authorization: Basic <base64(username:password)>` header. Configured via `I3X_BASIC_AUTH_USERNAME` and `I3X_BASIC_AUTH_PASSWORD`.
2. **OAuth2 / OpenID Connect bearer tokens** - the client sends an `Authorization: Bearer <JWT>` header. Configured via `I3X_OAUTH2_AUTHORITY` (and optionally `I3X_OAUTH2_AUDIENCE` / `I3X_OAUTH2_ISSUER`). Access tokens are validated against the authority's OIDC metadata (issuer, signing keys/JWKS, audience and expiry). Signing keys are discovered and cached automatically.

You may configure one or both methods. If **neither** is configured, the API fails closed and returns HTTP 503. Regardless of configuration, the health/capabilities endpoint (`GET /v1/info`), the Swagger UI / OpenAPI documents, and CORS preflight (`OPTIONS`) requests remain open.

### Example: OAuth2 with Microsoft Entra ID
```bash
# Enable OAuth2 bearer-token authentication against an Entra ID tenant.
export I3X_OAUTH2_AUTHORITY="https://login.microsoftonline.com/<tenant-id>/v2.0"
export I3X_OAUTH2_AUDIENCE="api://<application-client-id>"
# Optional: pin the expected issuer (otherwise taken from the authority metadata).
export I3X_OAUTH2_ISSUER="https://login.microsoftonline.com/<tenant-id>/v2.0"
```

Clients then acquire a token from the authority and call the API with it:
```bash
curl -H "Authorization: Bearer $ACCESS_TOKEN" https://<host>/v1/objects
```

To use HTTP Basic instead (or in addition), set:
```bash
export I3X_BASIC_AUTH_USERNAME="i3x"
export I3X_BASIC_AUTH_PASSWORD="<strong-password>"
```
