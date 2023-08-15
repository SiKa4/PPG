using IronPython.Hosting;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using static IronPython.Modules._ast;

internal class Program
{
    private const string HeartRateServiceId = "180d";
    private const string HeartRateCharacteristicId = "00002a37-0000-1000-8000-00805f9b34fb";

    private const string PPG_ID = "5c80";
    private const string PMD_CONTROL = "5c81";
    private const string PMD_DATA_UUID = "5c82";

    private static BluetoothLEDevice bluetoothLeDevice;

    [STAThread]
    static void Main(string[] args)
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
            socket.SendTo(Encoding.UTF8.GetBytes(message), ip);
            Console.WriteLine(message);
        };

        string? deviceId = null;

        //python parser

        var engine = Python.CreateEngine();
        var scriptSource = engine.CreateScriptSourceFromFile("./parser.py");

        dynamic scope = engine.CreateScope();
        scriptSource.Execute(scope);
        dynamic myClass = scope.HRParser();
        ///

        deviceWatcher.Added += async (sender, device) =>
        {
            if (!device.Name.Contains(deviceName) || device.Id == deviceId) return;

            BluetoothLEDevice bluetoothLeDevice = await BluetoothLEDevice.FromIdAsync(device.Id);
            GattDeviceServicesResult result = await bluetoothLeDevice.GetGattServicesAsync();

            if (result == null && result.Status != GattCommunicationStatus.Success)
            {
                Send("result == null && result.Status != GattCommunicationStatus.Success");
                return;
            }

            var services = result.Services;
            var service = services.FirstOrDefault(svc => svc.Uuid.ToString("N").Substring(4, 4) == HeartRateServiceId);

            if (service == null)
            {
                Send("service == null");
                return;
            }

            var charactiristicResult = await service.GetCharacteristicsAsync();

            if (charactiristicResult.Status != GattCommunicationStatus.Success)
            {
                Send("error: charactiristicResult.Status != GattCommunicationStatus.Success");
                return;
            }

            var characteristic = charactiristicResult.Characteristics.FirstOrDefault(chr => chr.Uuid.ToString() == HeartRateCharacteristicId);

            if (service == null || characteristic == null) 
            {
                Send("error: service == null || characteristic == null");
                return;
            } 

            GattCommunicationStatus status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify); ;

            if (status != GattCommunicationStatus.Success)
            {
                Send("error: status != GattCommunicationStatus.Success");
                return;
            }

            Send($"connected: {device.Name}");

            deviceId = device.Id;

            var service_ppg = services.FirstOrDefault(svc => svc.Uuid.ToString().Contains(PPG_ID));

            var charactiristicResult_PPG = await service_ppg.GetCharacteristicsAsync();

            var characteristic_PPG_Read = charactiristicResult_PPG.Characteristics.FirstOrDefault(chr => chr.Uuid.ToString().Contains(PMD_DATA_UUID));

            if (characteristic_PPG_Read == null)
            {
                Send("error: characteristic_PPG_Read == null");
                return;
            }

            var characteristic_PPG_Write = charactiristicResult_PPG.Characteristics.FirstOrDefault(chr => chr.Uuid.ToString().Contains(PMD_CONTROL));

            byte[] PPG_SETTING = new byte[] { 2, 1, 0, 1, 55, 0, 1, 1, 22, 0, 4, 1, 4 };
            await characteristic_PPG_Write.WriteValueAsync(PPG_SETTING.AsBuffer(), GattWriteOption.WriteWithoutResponse);

            bool isWearing = true;

            characteristic.ValueChanged += (gattCharacteristic, eventArgs) =>
            {
                var value = BitConverter.ToInt16(eventArgs.CharacteristicValue.ToArray().Reverse().ToArray(), 0);
                if (!isWearing) value = 0;
                Send($"hr: {value}");
            };

            int iterationsOfNotBeingOnHand = 4;
            characteristic_PPG_Read.ValueChanged += (gattCharacteristic, eventArgs) =>
            {
                var arrayByte = eventArgs.CharacteristicValue.ToArray();
                var result = myClass.parse_ppg(arrayByte);
                List<double> x = ConvertedDynamicInDoubleList(result["x"]);

                if (x.Count >= 35 && x.Count <= 43 && iterationsOfNotBeingOnHand > 0)
                    iterationsOfNotBeingOnHand--;
                else if ((x.Count < 35 || x.Count > 43) && iterationsOfNotBeingOnHand < 6)
                    iterationsOfNotBeingOnHand++;

                if (iterationsOfNotBeingOnHand == 0) isWearing = true;
                else if (iterationsOfNotBeingOnHand == 6) isWearing = false;

            };
        };


        deviceWatcher.Removed += (sender, update) =>
        {
            if (deviceId != null && update.Id == deviceId)
            {
                Send($"disconnected: {deviceId}");
                deviceId = null;
                deviceWatcher.Stop();
            }
        };

        deviceWatcher.Stopped += (sender, args) => deviceWatcher.Start();
        deviceWatcher.EnumerationCompleted += (sender, args) => deviceWatcher.Stop();

        deviceWatcher.Start();

        while (true)
            Thread.Sleep(50);
    }


    static List<double> ConvertedDynamicInDoubleList(dynamic diction)
    {
        List<double> converted = new List<double>();
        foreach(var i in diction)
        {
            converted.Add(i);
        }
        return converted;
    }
}
