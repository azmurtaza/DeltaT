using System.Text.Json;
using DeltaT.Core.Weather;
using Xunit;

namespace DeltaT.Core.Tests;

public class ReverseGeocodeNameTests
{
    private static string? Resolve(string json) =>
        AmbientService.ResolvePlaceName(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void PrefersDistrictCity_OverRuralUnionCouncil()
    {
        // The real BigDataCloud shape for a 100 m WiFi fix in Citi Housing, Sialkot:
        // city is the village "Daska Kalan", but the district is "Sialkot District".
        const string json = """
        {
          "city": "Daska Kalan",
          "locality": "Daska Kalan",
          "principalSubdivision": "Punjab",
          "countryName": "Pakistan",
          "localityInfo": {
            "administrative": [
              { "order": 4, "name": "Pakistan", "description": "republic in Asia" },
              { "order": 5, "name": "Punjab", "description": "province of Pakistan" },
              { "order": 6, "name": "Gujranwala Division", "description": "division of Punjab, Pakistan" },
              { "order": 7, "name": "Sialkot District", "description": "district of Punjab, Pakistan" },
              { "order": 8, "name": "Daska Kalan", "description": "human settlement in Pakistan" },
              { "order": 9, "name": "Daska Tehsil", "description": "tehsil in Punjab, Pakistan" }
            ]
          }
        }
        """;
        Assert.Equal("Sialkot", Resolve(json));
    }

    [Fact]
    public void UrbanCoordinate_StillResolvesToItsCity()
    {
        // Someone actually in the city: city == the district's principal city, so the
        // district-first rule returns the same recognizable name, never something worse.
        const string json = """
        {
          "city": "Lahore",
          "principalSubdivision": "Punjab",
          "localityInfo": { "administrative": [
            { "order": 7, "name": "Lahore District", "description": "district of Punjab, Pakistan" },
            { "order": 8, "name": "Lahore", "description": "provincial capital" }
          ] }
        }
        """;
        Assert.Equal("Lahore", Resolve(json));
    }

    [Fact]
    public void NoDistrictUnit_FallsBackToCity()
    {
        const string json = """
        { "city": "Reykjavik", "locality": "Reykjavik", "principalSubdivision": "Capital Region",
          "localityInfo": { "administrative": [
            { "order": 6, "name": "Capital Region", "description": "region of Iceland" }
          ] } }
        """;
        Assert.Equal("Reykjavik", Resolve(json));
    }

    [Fact]
    public void NoAdministrativeData_FallsBackThroughCityThenLocality()
    {
        Assert.Equal("Springfield", Resolve("""{ "city": "Springfield", "countryName": "USA" }"""));
        Assert.Equal("Shelbyville", Resolve("""{ "locality": "Shelbyville" }"""));
    }

    [Fact]
    public void MetropolitanCitySuffix_IsStripped()
    {
        const string json = """
        { "city": "Some Ward",
          "localityInfo": { "administrative": [
            { "order": 7, "name": "Seoul Metropolitan City", "description": "metropolitan city of South Korea" }
          ] } }
        """;
        Assert.Equal("Seoul", Resolve(json));
    }
}
