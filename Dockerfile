FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["XivMediaPlayer.Shared/XivMediaPlayer.Shared.csproj", "XivMediaPlayer.Shared/"]
COPY ["XivMediaPlayer.Server/XivMediaPlayer.Server.csproj", "XivMediaPlayer.Server/"]
RUN dotnet restore "XivMediaPlayer.Server/XivMediaPlayer.Server.csproj"
COPY . .
WORKDIR "/src/XivMediaPlayer.Server"
RUN dotnet build "XivMediaPlayer.Server.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "XivMediaPlayer.Server.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# The database file will be created in /data
VOLUME /data
ENV ConnectionStrings__DefaultConnection="Data Source=/data/XivMediaPlayer.db"

ENTRYPOINT ["dotnet", "XivMediaPlayer.Server.dll"]
