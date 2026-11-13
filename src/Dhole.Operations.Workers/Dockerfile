# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG GITHUB_ACTOR=github-actions
WORKDIR /src
COPY . .
RUN --mount=type=secret,id=github_token,required=true \
    export NuGetPackageSourceCredentials_github="Username=${GITHUB_ACTOR};Password=$(cat /run/secrets/github_token);ValidAuthenticationTypes=Basic" && \
    dotnet restore src/Dhole.Operations.Workers/Dhole.Operations.Workers.csproj --configfile NuGet.Config
RUN dotnet publish src/Dhole.Operations.Workers/Dhole.Operations.Workers.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
ENV TZ=America/Costa_Rica
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Dhole.Operations.Workers.dll"]
