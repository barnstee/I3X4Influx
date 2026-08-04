using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace I3X4Influx.Controllers
{
    /// <summary>
    /// Implements the i3X 1.0 Subscriptions family (except value/history writes):
    /// create, register, unregister, list, delete, sync and stream (SSE).
    ///
    /// Azure Data Explorer has no native change feed, so both sync and stream poll
    /// the telemetry measurement for values newer than each monitored element's high-water mark and
    /// bundle them into <see cref="SyncBatch"/>es via the shared <see cref="SubscriptionStore"/>.
    /// </summary>
    [ApiController]
    [Route("v1/subscriptions")]
    public sealed class SubscriptionsController : ControllerBase
    {
        private readonly InfluxDataService _influx;
        private readonly SubscriptionStore _store;

        public SubscriptionsController(InfluxDataService influx, SubscriptionStore store)
        {
            _influx = influx;
            _store = store;
            _influx.Connect();
        }

        // POST /v1/subscriptions
        [HttpPost]
        public ActionResult<SuccessResponse<CreateSubscriptionResponse>> Create([FromBody] CreateSubscriptionRequest request)
        {
            if (request is null || string.IsNullOrEmpty(request.ClientId))
            {
                return BadRequest(Error(400, "clientId is required."));
            }

            var sub = _store.Create(request.ClientId, request.DisplayName);
            var response = new CreateSubscriptionResponse(sub.ClientId, sub.SubscriptionId, sub.DisplayName);
            return Ok(new SuccessResponse<CreateSubscriptionResponse>(true, response));
        }

        // POST /v1/subscriptions/register
        [HttpPost("register")]
        public ActionResult<BulkResponse<object>> Register([FromBody] RegisterMonitoredItemsRequest request)
        {
            if (!TryResolve(request?.SubscriptionId, request?.ClientId, out var sub, out var error))
            {
                return error;
            }

            int maxDepth = request.MaxDepth ?? 1;

            // Validate each elementId against the address space so unknown ids fail per-item with a 404,
            // rather than being silently registered.
            var hierarchy = new Isa95Hierarchy(_influx.GetIsa95LeafAssets());

            var items = (request.ElementIds ?? Array.Empty<string>()).Select(id =>
            {
                if (string.IsNullOrEmpty(id) || !hierarchy.TryGet(id, out _))
                {
                    return BulkResultItem<object>.NotFound(id, "Object not found");
                }

                // No high-water mark is seeded here: like an OPC UA monitored item, the first poll delivers
                // the element's current value and only later polls are limited to newer readings. Seeding
                // with the registration time would leave a subscription silent until the source produced a
                // reading with a timestamp past it, which for slowly changing tags can be indefinitely.
                sub.AddElements(new[] { id }, maxDepth);
                return BulkResultItem<object>.Ok(id, null);
            }).ToList();

            return Ok(new BulkResponse<object>(items.All(i => i.Success), items));
        }

        // POST /v1/subscriptions/unregister
        [HttpPost("unregister")]
        public ActionResult<BulkResponse<object>> Unregister([FromBody] UnregisterMonitoredItemsRequest request)
        {
            if (!TryResolve(request?.SubscriptionId, request?.ClientId, out var sub, out var error))
            {
                return error;
            }

            // Validate each elementId so unknown ids are reported per-item rather than silently succeeding.
            var hierarchy = new Isa95Hierarchy(_influx.GetIsa95LeafAssets());

            var items = (request.ElementIds ?? Array.Empty<string>()).Select(id =>
            {
                if (string.IsNullOrEmpty(id) || !hierarchy.TryGet(id, out _))
                {
                    return BulkResultItem<object>.NotFound(id, "Object not found");
                }

                sub.RemoveElements(new[] { id });
                return BulkResultItem<object>.Ok(id, null);
            }).ToList();

            return Ok(new BulkResponse<object>(items.All(i => i.Success), items));
        }

        // POST /v1/subscriptions/list
        [HttpPost("list")]
        public ActionResult<BulkResponse<SubscriptionDetail>> List([FromBody] ListSubscriptionsRequest request)
        {
            if (request is null || string.IsNullOrEmpty(request.ClientId))
            {
                return BadRequest(Error(400, "clientId is required."));
            }

            var items = (request.SubscriptionIds ?? Array.Empty<string>()).Select(subId =>
            {
                if (!_store.TryGet(subId, out var sub) || sub.ClientId != request.ClientId)
                {
                    return SubscriptionNotFound(subId);
                }

                var monitored = sub.MonitoredElements
                    .Select(kv => new Dictionary<string, object>
                    {
                        ["elementId"] = kv.Key,
                        ["maxDepth"] = kv.Value
                    })
                    .ToList();

                var detail = new SubscriptionDetail(sub.SubscriptionId, monitored, sub.DisplayName);
                return new BulkResultItem<SubscriptionDetail>
                {
                    Success = true,
                    SubscriptionId = subId,
                    Result = detail
                };
            }).ToList();

            return Ok(new BulkResponse<SubscriptionDetail>(items.All(i => i.Success), items));
        }

        // POST /v1/subscriptions/delete
        [HttpPost("delete")]
        public ActionResult<BulkResponse<object>> Delete([FromBody] DeleteSubscriptionsRequest request)
        {
            if (request is null || string.IsNullOrEmpty(request.ClientId))
            {
                return BadRequest(Error(400, "clientId is required."));
            }

            var items = (request.SubscriptionIds ?? Array.Empty<string>()).Select(subId =>
            {
                if (!_store.TryGet(subId, out var sub) || sub.ClientId != request.ClientId)
                {
                    return SubscriptionNotFoundObj(subId);
                }

                _store.Delete(subId);
                return new BulkResultItem<object>
                {
                    Success = true,
                    SubscriptionId = subId
                };
            }).ToList();

            return Ok(new BulkResponse<object>(items.All(i => i.Success), items));
        }

        // POST /v1/subscriptions/sync
        // Returns HTTP 200 normally, or 206 if updates were dropped from the staging queue due to overflow.
        [HttpPost("sync")]
        public ActionResult<SuccessResponse<IReadOnlyList<SyncBatch>>> Sync([FromBody] SyncRequest request)
        {
            if (!TryResolve(request?.SubscriptionId, request?.ClientId, out var sub, out var error))
            {
                return error;
            }

            // Acknowledge previously received batches first.
            if (request.LastSequenceNumber.HasValue)
            {
                sub.Acknowledge(request.LastSequenceNumber.Value);
            }

            // Poll for new values and stage them as a fresh batch.
            bool noOverflow = StageNewValues(sub);

            var batches = sub.PendingSnapshot();
            var body = new SuccessResponse<IReadOnlyList<SyncBatch>>(true, batches);

            if (!noOverflow)
            {
                return StatusCode(StatusCodes.Status206PartialContent, body);
            }

            return Ok(body);
        }

        // POST /v1/subscriptions/stream (Server-Sent Events)
        [HttpPost("stream")]
        public async Task Stream([FromBody] StreamRequest request, CancellationToken cancellationToken)
        {
            if (request is null
                || string.IsNullOrEmpty(request.SubscriptionId)
                || !_store.TryGet(request.SubscriptionId, out var sub)
                || sub.ClientId != request.ClientId)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            Response.Headers.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
            Response.Headers.Connection = "keep-alive";

            int pollMs = GetStreamPollMs();

            // Only a single stream is allowed per subscription. Registering this stream cleanly cancels any
            // previously active stream, and gives us a token that fires if a later stream supersedes us.
            var streamCts = sub.BeginStream();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, streamCts.Token);
            CancellationToken token = linkedCts.Token;

            try
            {
                // Flush the response headers and an initial SSE comment immediately, so the stream is
                // acknowledged as "open" right away (before the first ADX poll). This ensures opening a new
                // stream succeeds promptly even while the superseded stream is still winding down.
                await Response.WriteAsync(": stream opened\n\n", token).ConfigureAwait(false);
                await Response.Body.FlushAsync(token).ConfigureAwait(false);

                while (!token.IsCancellationRequested)
                {
                    StageNewValues(sub);

                    foreach (var batch in sub.PendingSnapshot())
                    {
                        string json = JsonSerializer.Serialize(batch);
                        await Response.WriteAsync($"data: {json}\n\n", token).ConfigureAwait(false);
                        await Response.Body.FlushAsync(token).ConfigureAwait(false);

                        // In stream mode each batch is delivered once, then acknowledged.
                        sub.Acknowledge(batch.SequenceNumber);
                    }

                    await Task.Delay(pollMs, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected, or this stream was superseded by a newer one. Either way the SSE
                // stream ends cleanly (no error) so the previously connected client sees a normal close.
            }
            finally
            {
                sub.EndStream(streamCts);
            }
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        /// <summary>
        /// Query the telemetry measurement for the latest value per monitored element that is newer than the
        /// element's high-water mark, advance the mark, and stage them as a batch. Returns false if
        /// the staging queue overflowed.
        /// </summary>
        private bool StageNewValues(SubscriptionStore.Subscription sub)
        {
            var elements = sub.Elements;
            if (elements.Count == 0)
            {
                return true;
            }

            // Registered element ids are I3X object ids (variable ids "<Subject>::<Name>" or asset Subjects).
            // Resolve them through the ISA-95 hierarchy to the underlying telemetry Subjects, then map each
            // telemetry (Subject, Name) reading back to the registered element id(s) that requested it.
            var hierarchy = new Isa95Hierarchy(_influx.GetIsa95LeafAssets());

            var subjects = new HashSet<string>(StringComparer.Ordinal);
            // (Subject, Name) -> variable element id, for variable registrations.
            var variableByKey = new Dictionary<(string, string), string>();
            // Subject -> asset element id, for asset registrations (deliver all of the asset's variables).
            var assetBySubject = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var elementId in elements)
            {
                if (!hierarchy.TryGet(elementId, out var node))
                {
                    continue;
                }

                if (node.Kind == Isa95Hierarchy.NodeKind.Variable)
                {
                    if (string.IsNullOrEmpty(node.Subject))
                    {
                        continue;
                    }

                    subjects.Add(node.Subject);
                    variableByKey[(node.Subject, node.VariableName)] = elementId;
                }
                else
                {
                    // A container (asset or ISA-95 level) monitors everything beneath it. One asset regularly
                    // spans several dataset writers, so registering only node.Subject would deliver updates
                    // for a single telemetry tag.
                    foreach (var subject in CollectSubjects(hierarchy, node))
                    {
                        subjects.Add(subject);
                        assetBySubject[subject] = elementId;
                    }
                }
            }

            if (subjects.Count == 0)
            {
                return true;
            }

            var rows = _influx.GetLatestValues(subjects);

            var updates = new List<SyncUpdateEntry>();
            foreach (var row in rows)
            {
                string subject = Str(row, "Subject");
                string name = Str(row, "Name");
                if (string.IsNullOrEmpty(subject))
                {
                    continue;
                }

                DateTime ts = row.TryGetValue("Timestamp", out var t) && t is DateTime dt ? dt : DateTime.MinValue;

                // A telemetry reading may satisfy a specific variable registration and/or an asset
                // registration (which monitors all of its variables).
                var targets = new List<string>(2);
                if (variableByKey.TryGetValue((subject, name), out var variableId))
                {
                    targets.Add(variableId);
                }
                if (assetBySubject.TryGetValue(subject, out var assetId))
                {
                    targets.Add(assetId);
                }

                foreach (var elementId in targets)
                {
                    bool isAssetTarget = assetBySubject.TryGetValue(subject, out var aId) && aId == elementId;

                    // Variables track one high-water mark by their element id. Assets track one per contained
                    // variable ("<assetId>::<name>"), falling back to the asset's base seed for the first poll.
                    string markKey = isAssetTarget ? elementId + "::" + name : elementId;

                    DateTime seen = DateTime.MinValue;
                    bool hasSeen = sub.LastSeen.TryGetValue(markKey, out seen)
                                   || (isAssetTarget && sub.LastSeen.TryGetValue(elementId, out seen));

                    if (hasSeen && ts <= seen)
                    {
                        continue;
                    }

                    updates.Add(new SyncUpdateEntry(
                        elementId,
                        row.GetValueOrDefault("Value"),
                        "Good",
                        ts == DateTime.MinValue ? string.Empty : ToRfc3339(ts)));

                    if (ts != DateTime.MinValue)
                    {
                        sub.LastSeen[markKey] = ts;
                    }
                }
            }

            return sub.StageBatch(updates);
        }

        /// <summary>
        /// Telemetry Subjects of a node and of all its descendants, so a registered container monitors every
        /// dataset writer beneath it.
        /// </summary>
        private static IReadOnlyCollection<string> CollectSubjects(Isa95Hierarchy hierarchy, Isa95Hierarchy.Node node)
        {
            var subjects = new HashSet<string>(StringComparer.Ordinal);
            Collect(node);
            return subjects;

            void Collect(Isa95Hierarchy.Node current)
            {
                if (!string.IsNullOrEmpty(current.Subject))
                {
                    subjects.Add(current.Subject);
                }

                foreach (var child in hierarchy.ChildrenOf(current.ElementId))
                {
                    Collect(child);
                }
            }
        }

        private bool TryResolve(string subscriptionId, string clientId, out SubscriptionStore.Subscription sub, out ActionResult error)
        {
            sub = null;
            error = null;

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(subscriptionId))
            {
                error = BadRequest(Error(400, "clientId and subscriptionId are required."));
                return false;
            }

            if (!_store.TryGet(subscriptionId, out sub) || sub.ClientId != clientId)
            {
                error = NotFound(Error(404, $"Subscription '{subscriptionId}' not found."));
                return false;
            }

            return true;
        }

        private static int GetStreamPollMs()
        {
            string raw = Environment.GetEnvironmentVariable("I3X_STREAM_POLL_MS");
            return int.TryParse(raw, out int ms) && ms >= 250 ? ms : 2000;
        }

        private static ErrorResponse Error(int status, string detail) =>
            new(new ErrorDetail(status == 404 ? "Not Found" : "Bad Request", status, detail));

        private static BulkResultItem<SubscriptionDetail> SubscriptionNotFound(string subscriptionId) =>
            new()
            {
                Success = false,
                SubscriptionId = subscriptionId,
                ResponseDetail = new ErrorDetail("Not Found", 404, "Subscription not found")
            };

        private static BulkResultItem<object> SubscriptionNotFoundObj(string subscriptionId) =>
            new()
            {
                Success = false,
                SubscriptionId = subscriptionId,
                ResponseDetail = new ErrorDetail("Not Found", 404, "Subscription not found")
            };

        // i3X requires RFC 3339 UTC timestamps terminated by "Z". DateTimeOffset's round-trip ("o") format
        // renders the zero offset as "+00:00" instead, which the conformance suite rejects, so the UTC
        // designator is appended explicitly.
        private static string ToRfc3339(DateTime dt) =>
            DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                .ToString("yyyy-MM-ddTHH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);

        private static string Str(Dictionary<string, object> row, string key) =>
            row.TryGetValue(key, out var v) ? v?.ToString() ?? string.Empty : string.Empty;
    }
}
