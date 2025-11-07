// Copyright (c) Duende Software. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using BlazorBffOnboarding.Client;

namespace BlazorBffOnboarding;

public static class WeatherEndpointExtensions
{
    public static void MapWeatherEndpoints(this WebApplication app)
    {
        app.MapGet("/WeatherForecast", async (IWeatherClient weatherClient) => await weatherClient.GetWeatherForecasts())
            .RequireAuthorization()
            .AsBffApiEndpoint();
    }


}
