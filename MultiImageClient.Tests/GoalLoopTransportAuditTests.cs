using System.Linq;
using System.Text.Json;
using MultiImageClient;
using Xunit;

namespace MultiImageClient.Tests
{
    // Regressions from the 2026-09-12 audit of failed production and local
    // loops: replies cut off at the 16,000-token output cap, an xAI critique
    // request rejected HTTP 413, and critic replies that ended in a lone
    // Markdown fence.
    public class GoalLoopTransportAuditTests
    {
        [Theory]
        [InlineData("{\"a\":1}", "{\"a\":1}")]
        [InlineData("```json\n{\"a\":1}\n```", "{\"a\":1}")]
        [InlineData("```\n{\"a\":1}\n```", "{\"a\":1}")]
        [InlineData("```json{\"a\":1}```", "{\"a\":1}")]
        // Opus 5 critic reply, 2026-09-12: no opening fence, closing fence present.
        [InlineData("{\"a\":1}\n```", "{\"a\":1}")]
        // Opening fence only.
        [InlineData("```json\n{\"a\":1}", "{\"a\":1}")]
        // A fence inside a string value is content and stays.
        [InlineData("{\"a\":\"x ``` y\"}", "{\"a\":\"x ``` y\"}")]
        public void MarkdownFencesAreStrippedIndependentlyAtEachEnd(string raw, string expected)
        {
            Assert.Equal(expected, UiGoalLoopProtocol.StripMarkdownFence(raw));
            using var doc = JsonDocument.Parse(UiGoalLoopProtocol.StripMarkdownFence(raw));
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        }

        [Fact]
        public void XaiRequestCapIsTheLargestProbedPassingSize()
        {
            Assert.Equal(48L * 1024 * 1024, ManagerCatalog.XaiLimits.MaxRequestBytes);
            // Ten 6 MB PNGs (the 81.9 MB production critique request as
            // base64) now register as budget pressure and switch to
            // full-resolution JPEG instead of a provider 413.
            var sizes = Enumerable.Repeat(6_000_000L, 10).ToList();
            Assert.True(UiGoalLoopImageTransport.RequestBudgetPressure(ManagerCatalog.XaiLimits, sizes));
            var plan = UiGoalLoopImageTransport.Make(ManagerCatalog.XaiLimits, new UiGoalLoopImageInfo("image/png", 2048, 2048, 6_000_000), 10, budgetPressure: true);
            Assert.Equal("jpeg", plan.Mode);
            Assert.Equal(2048, plan.Width);
        }

        [Fact]
        public void LoopGetCopyOmitsWirePayloadsButReportsTheirSize()
        {
            var entry = new UiGoalLoopEntry
            {
                Index = 3,
                Kind = UiGoalLoopKinds.Review,
                Text = "{}",
                WireRequest = new string('r', 1000),
                WireResponse = new string('s', 250),
            };
            var slim = entry.CloneWithoutWire();
            Assert.Null(slim.WireRequest);
            Assert.Null(slim.WireResponse);
            Assert.Equal(1000, slim.WireOmitted!.RequestChars);
            Assert.Equal(250, slim.WireOmitted.ResponseChars);
            Assert.Equal(3, slim.Index);
            // The original is untouched and a plain clone keeps the payloads.
            Assert.Equal(1000, entry.WireRequest!.Length);
            Assert.Null(entry.WireOmitted);
            Assert.Equal(250, entry.Clone().WireResponse!.Length);

            var json = JsonSerializer.Serialize(slim, UiGoalLoopJson.Options);
            Assert.Contains("\"wireOmitted\":{\"requestChars\":1000,\"responseChars\":250}", json);
            Assert.DoesNotContain("wireRequest", json);

            var plain = new UiGoalLoopEntry { Index = 4, Kind = UiGoalLoopKinds.Note, Text = "n" };
            Assert.Null(plain.CloneWithoutWire().WireOmitted);
        }
    }
}
