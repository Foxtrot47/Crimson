using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Serilog;

namespace Crimson.Tests;

public sealed class StoreProductDetailsTests
{
    private const string ProductDetailsJson = """
        {
          "data": {
            "Product": {
              "sandbox": {
                "configuration": [
                  {
                    "__typename": "StoreConfiguration",
                    "configs": {
                      "developerDisplayName": "Example Developer",
                      "gameWebsite": "https://example.test/game",
                      "keyImages": [
                        {
                          "__typename": "KeyImage",
                          "type": "ProductLogo",
                          "url": "https://cdn.example/logo.png",
                          "alt": "Example logo"
                        }
                      ],
                      "pcReleaseDate": "2025-04-15T00:00:00.000Z",
                      "productDisplayName": "Example Game",
                      "publisherDisplayName": "Example Publisher",
                      "shortDescription": "A useful short description.",
                      "supportedAudio": ["English"],
                      "supportedText": ["English", "French"],
                      "tags": [
                        { "name": "Cloud Saves", "groupName": "feature" },
                        { "name": "Action", "groupName": "genre" }
                      ],
                      "technicalRequirements": {
                        "windows": [
                          {
                            "title": "OS",
                            "minimum": "Windows 10",
                            "recommended": "Windows 11"
                          }
                        ]
                      }
                    }
                  },
                  {
                    "__typename": "HomeConfiguration",
                    "configs": {
                      "longDescription": "A complete game description.",
                      "keyImages": [
                        {
                          "__typename": "KeyImage",
                          "type": "featuredMedia",
                          "url": "https://cdn.example/hero.jpg",
                          "alt": "Example scene"
                        },
                        {
                          "__typename": "Video",
                          "type": "heroCarouselVideo",
                          "url": "https://cdn.example/trailer.mp4",
                          "alt": "Example trailer"
                        },
                        {
                          "__typename": "KeyImage",
                          "type": "ProductLogo",
                          "url": "https://cdn.example/logo.png",
                          "alt": "Duplicate logo"
                        }
                      ]
                    }
                  }
                ]
              }
            }
          }
        }
        """;

    [Fact]
    public void Parse_MergesStoreAndHomeConfiguration()
    {
        var details = Assert.IsType<StoreProductDetails>(StoreProductDetailsParser.Parse(ProductDetailsJson));

        Assert.Equal("Example Game", details.ProductName);
        Assert.Equal("Example Developer", details.DeveloperName);
        Assert.Equal("Example Publisher", details.PublisherName);
        Assert.Equal("A useful short description.", details.ShortDescription);
        Assert.Equal("A complete game description.", details.LongDescription);
        Assert.Equal("https://example.test/game", details.WebsiteUrl);
        Assert.Equal(new DateTimeOffset(2025, 4, 15, 0, 0, 0, TimeSpan.Zero), details.ReleaseDate);
        Assert.Collection(
            details.Images,
            image =>
            {
                Assert.Equal("ProductLogo", image.Type);
                Assert.False(image.IsVideo);
            },
            image => Assert.Equal("featuredMedia", image.Type),
            image =>
            {
                Assert.Equal("heroCarouselVideo", image.Type);
                Assert.True(image.IsVideo);
            });
        Assert.Equal(["Cloud Saves", "Action"], details.Tags.Select(tag => tag.Name));
        Assert.Equal(["English"], details.SupportedAudioLanguages);
        Assert.Equal(["English", "French"], details.SupportedTextLanguages);
        var requirement = Assert.Single(details.WindowsRequirements);
        Assert.Equal("OS", requirement.Title);
        Assert.Equal("Windows 10", requirement.Minimum);
        Assert.Equal("Windows 11", requirement.Recommended);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":{\"Product\":{\"sandbox\":{\"configuration\":[]}}}}")]
    [InlineData("not json")]
    public void Parse_ReturnsNullWhenProductConfigurationIsUnavailable(string json)
    {
        Assert.Null(StoreProductDetailsParser.Parse(json));
    }

    [Fact]
    public async Task Repository_RequestsProductConfigurationByNamespace()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        using var storeClient = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            capturedRequest = request;
            capturedBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ProductDetailsJson, Encoding.UTF8, "application/json")
            };
        }));
        using var apiClient = new HttpClient();
        using var contentClient = new HttpClient();
        using var logger = new LoggerConfiguration().CreateLogger();
        var authManager = (AuthManager)RuntimeHelpers.GetUninitializedObject(typeof(AuthManager));
        var repository = new EpicGamesRepository(
            authManager,
            logger,
            apiClient,
            contentClient,
            storeClient);

        var details = await repository.FetchStoreProductDetails("example-namespace");

        Assert.NotNull(details);
        Assert.Equal(HttpMethod.Post, capturedRequest?.Method);
        Assert.Equal("https://launcher.store.epicgames.com/graphql", capturedRequest?.RequestUri?.AbsoluteUri);
        using var body = JsonDocument.Parse(Assert.IsType<string>(capturedBody));
        Assert.Equal(
            "example-namespace",
            body.RootElement.GetProperty("variables").GetProperty("sandboxId").GetString());
        Assert.Contains("StoreConfiguration", body.RootElement.GetProperty("query").GetString());
        Assert.Null(capturedRequest?.Headers.Authorization);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }
}
