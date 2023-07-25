using System;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation.Diagnostics;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Controls;
internal class Program
{
    private const string HeartRateServiceId = "180d";

    [STAThread]
    public static void Main(string[] args)
    {

        _ = new Mutex(true, "HRMonitor", out var prevInstance);
        if (prevInstance == false)
            return;

        var deviceName = args.Length > 0 ? args[0] : "Polar";
        
        string[] requestedProperties = { "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected" };
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var ip = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 8081);

        DeviceWatcher deviceWatcher =
            DeviceInformation.CreateWatcher(
                BluetoothLEDevice.GetDeviceSelectorFromPairingState(false),
                requestedProperties,
                DeviceInformationKind.AssociationEndpoint);

        var Send = (string message) =>
        {
            //socket.SendTo(Encoding.UTF8.GetBytes(message), ip);
            Console.WriteLine(message);
        };
        
        // Register event handlers before starting the watcher.
        // Added, Updated and Removed are required to get all nearby devices
        deviceWatcher.Updated += (sender, update) =>
        {
        };

        string? deviceId = null;

        deviceWatcher.Added += async (sender, device) =>
        {
            if (!device.Name.Contains(deviceName)) return;

            Send($"connected: {device.Name}");

            BluetoothLEDevice bluetoothLeDevice = await BluetoothLEDevice.FromIdAsync(device.Id);
            var result = await bluetoothLeDevice.GetGattServicesAsync();

            async void isCheckConnection(string error)
            {
                bluetoothLeDevice = await BluetoothLEDevice.FromIdAsync(device.Id);
                result = await bluetoothLeDevice.GetGattServicesAsync();
                if (result.Status != GattCommunicationStatus.Success)
                {
                    Send(error);
                    Thread.Sleep(1000);
                    isCheckConnection(error);
                }
                else return;
            }

            if (result.Status != GattCommunicationStatus.Success)
            {
                Send("error: result.Status != GattCommunicationStatus.Success");
                return;
            }

            var services = result.Services;
            var service = services.FirstOrDefault(svc => svc.Uuid.ToString("N").Substring(4, 4) == HeartRateServiceId);

            if (service == null)
            {
                Send("error: HEART RATE SERVICE not found");
                return;
            }

            var charactiristicResult = await service.GetCharacteristicsAsync();

            if (charactiristicResult.Status != GattCommunicationStatus.Success)
            {
                isCheckConnection("error: service.GetCharacteristicsAsync()");
            }

            var characteristic = charactiristicResult.Characteristics.FirstOrDefault(chr => chr.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify));

            void isCheckConnectionCharacteristic()
            {
                characteristic = charactiristicResult.Characteristics.FirstOrDefault(chr => chr.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify));
                if (characteristic == null)
                {

                    Send("error: GattCharacteristic with Notify not found");
                    Thread.Sleep(1000);
                    isCheckConnectionCharacteristic();
                }
                else
                {
                    return;
                }

            }

            if (characteristic == null)
            {
                isCheckConnectionCharacteristic();
            }

            var status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);

            if (status != GattCommunicationStatus.Success)
            {
                Send("error: status != GattCommunicationStatus.Success");
                return;
            }

            deviceId = device.Id;

            //ppg and hr listener 

            const string PPG_ID = "fb005c80-02e7-f387-1cad-8acd2d8df0c8";

            var service_ppg = services.FirstOrDefault(svc => svc.Uuid.ToString() == PPG_ID);

            var charactiristicResult_PPG = await service_ppg.GetCharacteristicsAsync();

            string PMD_DATA_UUID = "fb005c82-02e7-f387-1cad-8acd2d8df0c8";

            string PMD_CONTROL = "fb005c81-02e7-f387-1cad-8acd2d8df0c8";

            var characteristic_PPG_Read = charactiristicResult_PPG.Characteristics.FirstOrDefault(chr => chr.Uuid.ToString() == PMD_DATA_UUID);
            await characteristic_PPG_Read.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);

            var characteristic_PPG_Write = charactiristicResult_PPG.Characteristics.FirstOrDefault(chr => chr.Uuid.ToString() == PMD_CONTROL);

            byte[] PPG_SETTING = new byte[] { 2, 1, 0, 1, 55, 0, 1, 1, 22, 0, 4, 1, 4 };
            await characteristic_PPG_Write.WriteValueAsync(PPG_SETTING.AsBuffer(), GattWriteOption.WriteWithoutResponse);

            bool isWearing = false;
            int locationСounter = 3;
            characteristic.ValueChanged += (gattCharacteristic, eventArgs) =>
            {
                var value = BitConverter.ToInt16(eventArgs.CharacteristicValue.ToArray().Reverse().ToArray(), 0);

                //if (isWearing) Console.WriteLine("Надет");
                if (!isWearing) value = 0;
                Send($"hr: {value}");

            };

            characteristic_PPG_Read.ValueChanged += (gattCharacteristic, eventArgs) =>
            {
                var arrayByte = eventArgs.CharacteristicValue.ToArray();
                /* var answer = new HRParser().parsePpg(arrayByte);

                 var xObject = answer["x"];
                 var xList = (xObject as List<double>).Count != 0 ? (xObject as List<double>) : new List<double>();
                 Console.WriteLine(xList.Count);
                 if (xList.Count == 1 && locationСounter < 3) locationСounter++;
                 else if (xList.Count != 1 && locationСounter != 0) locationСounter--;

                 if (locationСounter == 3) isWearing = false;
                 else if (locationСounter == 0) isWearing = true;*/
                //Console.WriteLine(arrayByte.Length);
                if (arrayByte.Length == 228 || arrayByte.Length == 224 && locationСounter < 3) locationСounter++;
                else if (arrayByte.Length != 228 || arrayByte.Length != 224 && locationСounter != 0) locationСounter--;

                if (locationСounter == 3) isWearing = false;
                else if (locationСounter == 0 || arrayByte.Length == 229)
                {
                    locationСounter = 0;
                    isWearing = true;
                }
            };
        };



        deviceWatcher.Removed += (sender, update) =>
        {
            if (update.Id != deviceId) return;
            
            Send($"disconnected: {deviceId}");
            deviceId = null;
        };


        deviceWatcher.Start();
        while (true)
        {
            Thread.Sleep(50);
        }
    }
}
