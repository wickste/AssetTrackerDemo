using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Device.Location;
using System.Threading;
using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Client.Exceptions;
using System.Diagnostics;
using Microsoft.Azure.Devices.Shared;
using Newtonsoft.Json;
using Microsoft.Azure.Devices.Provisioning.Client.Transport;
using Microsoft.Azure.Devices.Provisioning.Client;
using System.Security.Cryptography;

namespace AssetTrackerDemo
{
    internal class Program
    {
        
        public static ManualResetEvent Failed = new ManualResetEvent(false);
        static void Main(string[] args)
        {
            TrackerDevice device = new TrackerDevice();
            Failed.WaitOne();
        }
    }

    public class TrackerDevice
    {
        private DeviceClient deviceClient;
        private GeoCoordinateWatcher watcher;
        private int interval = 5000;

        private const string DTMI = "dtmi:assetTrackerDemo:XFAssetTrackert0;1";
        private const string DEVICE_ID = "TODO";
        private const string SCOPE_ID= "TODO";
        private const string GROUP_KEY = "TODO";

        public TrackerDevice()
        {
            watcher = new GeoCoordinateWatcher();
            _ = Connect();
        }

        public async Task Connect()
        {
            try
            {
                deviceClient = await CreateDeviceClientAsync();                    
                deviceClient.SetConnectionStatusChangesHandler(ConnectionChangedHandler);
                await deviceClient.SetMethodDefaultHandlerAsync(CommandHandler, null);
                await deviceClient.SetDesiredPropertyUpdateCallbackAsync(PropertyUpdateHandler, null);

                TwinCollection reported = new TwinCollection();
                reported["FrameworkVersion"] = Environment.Version.ToString();
                reported["Manufacturer"] = "Contoso";
                reported["SDKVersion"] = typeof(DeviceClient).Assembly.GetName().Version.ToString();

                Twin twin = await deviceClient.GetTwinAsync();
                TwinCollection desired = twin.Properties.Desired;
                if (desired.Contains("Interval"))
                {
                    reported["Interval"] = new
                    {
                        value = desired["Interval"],
                        av = desired.Version,
                        ad = "Ack initial cloud value",
                        ac = 200
                    };
                    interval = (int)desired["Interval"] * 1000;
                }
                await deviceClient.UpdateReportedPropertiesAsync(reported);

                _ = Loop();
            }
            catch (Exception exc)
            {
                Console.WriteLine($"{DateTime.Now}: Exception trying to create/connect device:");
                while (exc != null)
                {
                    Console.WriteLine(exc.ToString());
                    exc = exc.InnerException;
                }
                Program.Failed.Set();
            }
        }

        private async Task<DeviceClient> CreateDeviceClientAsync()
        {
            HMACSHA256 hmac = new HMACSHA256(Convert.FromBase64String(GROUP_KEY));
            string deviceKey = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(DEVICE_ID)));

            using (var security = new SecurityProviderSymmetricKey(DEVICE_ID, deviceKey, deviceKey))
            {
                using (ProvisioningTransportHandler transport = new ProvisioningTransportHandlerMqtt(TransportFallbackType.TcpOnly))
                {
                    var provClient = ProvisioningDeviceClient.Create("global.azure-devices-provisioning.net", SCOPE_ID, security, transport);
                    string payload = JsonConvert.SerializeObject(new { modelId = DTMI });
                    ProvisioningRegistrationAdditionalData data = new ProvisioningRegistrationAdditionalData() { JsonData = payload };
                    DeviceRegistrationResult result = await provClient.RegisterAsync(data).ConfigureAwait(false);
                    if (result.Status == ProvisioningRegistrationStatusType.Assigned)
                    {
                        IAuthenticationMethod auth = new DeviceAuthenticationWithRegistrySymmetricKey(result.DeviceId, (security as SecurityProviderSymmetricKey).GetPrimaryKey());
                        return DeviceClient.Create(result.AssignedHub, auth, TransportType.Amqp, new ClientOptions() { ModelId = DTMI });
                    }
                    else
                    {
                        Console.Write($"Provisioning failed for {result.DeviceId} with status {result.Status} - error: {result.ErrorCode}-{result.ErrorMessage}");
                        return null;
                    }
                }
            }
        }


        public async Task Loop()
        {            
            watcher.TryStart(false, TimeSpan.FromMilliseconds(1000));
            while (deviceClient != null)
            {
                try
                {
                    var location = watcher.Position.Location;
                    if (!location.IsUnknown)
                    {
                        var telemetry = new
                        {
                            Location = new
                            {
                                lon = location.Longitude,
                                lat = location.Latitude,
                                alt = location.Altitude
                            }
                        };
                        Message msg = new Message(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(telemetry)));
                        msg.ContentType = "application/json";
                        msg.ContentEncoding = "UTF8";
                        await deviceClient.SendEventAsync(msg);
                    }
                    else
                    {
                        Debugger.Break();
                    }
                }
                catch (IotHubException exc)
                {
                    Debugger.Break();
                    Console.WriteLine($"{DateTime.Now}: IotHubException trying to send telemetry: {exc}");
                }
                catch (TimeoutException exc)
                {
                    Debugger.Break();
                    Console.WriteLine($"{DateTime.Now}: TimeoutException trying to send telemetry: {exc}");
                }
                catch (Exception exc)
                {
                    Console.WriteLine($"{DateTime.Now}: Exception trying to send telemetry:");
                    while (exc != null)
                    {
                        Console.WriteLine(exc.ToString());
                        exc = exc.InnerException;
                    }
                    Program.Failed.Set();
                }
                await Task.Delay(interval);
            }
        }

        private async Task PropertyUpdateHandler(TwinCollection desiredProperties, object userContext)
        {
            if (desiredProperties.Contains("Interval"))
            {
                Console.WriteLine($"{DateTime.Now}: writeable property update: {desiredProperties["Interval"]}");
                try
                {
                    interval = (int)desiredProperties["Interval"] * 1000;
                    TwinCollection reported = new TwinCollection();
                    reported["Interval"] = new
                    {
                        value = desiredProperties["Interval"],
                        av = desiredProperties.Version,
                        ac = 200,
                        ad = "Updated completed"
                    };
                    await deviceClient.UpdateReportedPropertiesAsync(reported);
                }
                catch (IotHubException exc)
                {
                    Debugger.Break();
                    Console.WriteLine($"{DateTime.Now}: IotHubException trying to ack writeable property update: {exc}");
                }
                catch (TimeoutException exc)
                {
                    Debugger.Break();
                    Console.WriteLine($"{DateTime.Now}: TimeoutException trying to ack writeable property update: {exc}");
                }
                catch (Exception exc)
                {
                    Console.WriteLine($"{DateTime.Now}: Unexpected exception trying to ack writeable property update:");
                    while (exc != null)
                    {
                        Console.WriteLine(exc.ToString());
                        exc = exc.InnerException;
                    }
                    Program.Failed.Set();
                }
            }
        }

        private Task<MethodResponse> CommandHandler(MethodRequest methodRequest, object userContext)
        {
            Console.WriteLine($"{DateTime.Now}: {methodRequest.Name} with payload {methodRequest.DataAsJson}");
            if (methodRequest.Name == "Reboot")
            {
                _ = Task.Run(async () =>
                {                    
                    await deviceClient.CloseAsync();
                    deviceClient.Dispose();
                    deviceClient = null;
                    await Task.Delay(5000);
                    _ = Connect();
                });                
            }
            return Task.FromResult(new MethodResponse(200));
        }

        private void ConnectionChangedHandler(ConnectionStatus status, ConnectionStatusChangeReason reason)
        {
            Console.WriteLine($"{DateTime.Now}: ConnectionStatus changed: {status} - {reason}");
        }
    }

}
