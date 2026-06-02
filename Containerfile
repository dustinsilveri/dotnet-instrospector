FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY DotnetIntrospector/DotnetIntrospector.csproj DotnetIntrospector/
RUN dotnet restore DotnetIntrospector/DotnetIntrospector.csproj

COPY DotnetIntrospector/. DotnetIntrospector/
RUN dotnet publish DotnetIntrospector/DotnetIntrospector.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends docker.io \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "DotnetIntrospector.dll"]
