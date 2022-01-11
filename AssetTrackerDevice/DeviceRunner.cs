using dtmi_assettrackerdemo;
using Rido.IoTClient;
using Rido.IoTClient.AzIoTHub;

namespace AssetTrackerDevice
{
    public class DeviceRunner : BackgroundService
    {
        private readonly ILogger<DeviceRunner> _logger;
        private readonly IConfiguration _configuration;

        public DeviceRunner(ILogger<DeviceRunner> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        const int default_interval = 5;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var locator = new Windows.Devices.Geolocation.Geolocator();
            var client = await xfassettrackert0.CreateAsync(_configuration.GetConnectionString("cs"), stoppingToken);
            
            client.Command_Reboot.OnCmdDelegate = async (cmd) =>
            {
                _logger.LogInformation("Command Reboot received");
                return await Task.FromResult(new Rido.IoTClient.EmptyCommandResponse());
            };

            client.Property_Interval.OnProperty_Updated = async p =>
            {
                var ack = new PropertyAck<int>(p.Name)
                {
                    Value = p.Value,
                    Status = 200,
                    Version = p.Version,
                    Description = "property accepted"
                };
                client.Property_Interval.PropertyValue.Value = p.Value;
                return await Task.FromResult(ack);
            };

            await client.Property_Interval.InitPropertyAsync(client.InitialState, default_interval, stoppingToken);

            client.Property_FrameworkVersion.PropertyValue = Environment.Version.ToString();
            client.Property_Manufacturer.PropertyValue = "Contoso";
            client.Property_SDKVersion.PropertyValue = (typeof(IoTHubPnPClient).Assembly.GetName().Version.ToString());

            await client.ReportPropertyAsync(client.AllReadOnlyProperties, stoppingToken);
            
            while (!stoppingToken.IsCancellationRequested)
            {
                var location = await locator.GetGeopositionAsync();
                await client.Telemetry_Location.SendTelemetryAsync(new Location 
                { 
                    lat = location.Coordinate.Latitude, 
                    lon = location.Coordinate.Longitude,
                    alt = location.Coordinate.Altitude.Value

                });
                _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
                await Task.Delay(client.Property_Interval.PropertyValue.Value * 1000, stoppingToken);
            }
        }
    }
}