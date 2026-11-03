FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ProjectPulse.Processor.csproj ./
RUN dotnet restore ProjectPulse.Processor.csproj

COPY . .
RUN dotnet publish ProjectPulse.Processor.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ARG PROJECT_PULSE_BUILD_VERSION=unknown
ENV ASPNETCORE_URLS=http://+:8080 \
    PROJECT_PULSE_BUILD_VERSION=${PROJECT_PULSE_BUILD_VERSION}
EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "ProjectPulse.Processor.dll"]
