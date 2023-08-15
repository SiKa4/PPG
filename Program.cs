using IronPython.Hosting;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

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
        BleDevice(args);

        while (true)
            Thread.Sleep(1);
    }

    static async Task BleDevice(string[] args)
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

        Send("error: disconnected1");

        //python parser

        var engine = Python.CreateEngine();
        var scriptSource = engine.CreateScriptSourceFromFile("./parser.py");

        dynamic scope = engine.CreateScope();
        scriptSource.Execute(scope);
        dynamic myClass = scope.HRParser();
        ///

        deviceWatcher.Added += async (sender, device) =>
        {
            if (!device.Name.Contains(deviceName)) return;

            BluetoothLEDevice bluetoothLeDevice;
            GattDeviceServicesResult result = null;
            async Task isCheckConnection()
            {
                try
                {
                    bluetoothLeDevice = await BluetoothLEDevice.FromIdAsync(device.Id);
                    result = await bluetoothLeDevice.GetGattServicesAsync();
                    if (result.Status != GattCommunicationStatus.Success)
                    {
                        Thread.Sleep(1000);
                        await isCheckConnection();
                    }
                }
                catch
                {
                    await isCheckConnection();
                }
            }

            if (result == null || result.Status != GattCommunicationStatus.Success) await isCheckConnection();

            var services = result.Services;
            var service = services.FirstOrDefault(svc => svc.Uuid.ToString("N").Substring(4, 4) == HeartRateServiceId);

            void isCheckServices()
            {
                service = services.FirstOrDefault(svc => svc.Uuid.ToString("N").Substring(4, 4) == HeartRateServiceId);
                if (service == null)
                {
                    Thread.Sleep(1000);
                    isCheckServices();
                }
            }

            if (service == null) isCheckServices();

            var charactiristicResult = await service.GetCharacteristicsAsync();

            if (charactiristicResult.Status != GattCommunicationStatus.Success)
                await isCheckConnection();

            var characteristic = charactiristicResult.Characteristics.FirstOrDefault(chr => chr.Uuid.ToString() == HeartRateCharacteristicId);

            async Task isCheckConnectionCharacteristic()
            {
                try
                {
                    characteristic = charactiristicResult.Characteristics.FirstOrDefault(chr => chr.Uuid.ToString() == HeartRateCharacteristicId);
                    //new initialization BLEDevice

                    bluetoothLeDevice = await BluetoothLEDevice.FromIdAsync(device.Id);
                    result = await bluetoothLeDevice.GetGattServicesAsync();
                    services = result.Services;
                    service = services.FirstOrDefault(svc => svc.Uuid.ToString("N").Substring(4, 4) == HeartRateServiceId);
                    if (service == null)
                        await isCheckConnectionCharacteristic();
                    else
                    {
                        charactiristicResult = await service.GetCharacteristicsAsync();

                        if (characteristic == null) await isCheckConnectionCharacteristic();
                    }
                }
                catch (Exception ex) { await isCheckConnectionCharacteristic(); }
            }

            if (characteristic == null) await isCheckConnectionCharacteristic();

            GattCommunicationStatus status;
            await CheckGattStatus();

            async Task CheckGattStatus()
            {
                try
                {
                    status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
                    if (status != null && status == GattCommunicationStatus.Success)
                        Send($"connected: {device.Name}");
                    else
                        await CheckGattStatus();
                }
                catch { await CheckGattStatus(); }
            }


            deviceId = device.Id;

            var service_ppg = services.FirstOrDefault(svc => svc.Uuid.ToString().Contains(PPG_ID));

            var charactiristicResult_PPG = await service_ppg.GetCharacteristicsAsync();

            var characteristic_PPG_Read = charactiristicResult_PPG.Characteristics.FirstOrDefault(chr => chr.Uuid.ToString().Contains(PMD_DATA_UUID));
            await characteristic_PPG_Read.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);

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
                Send("error: disconnected2");
                deviceId = null;
                deviceWatcher.Stop();
            }
        };

        deviceWatcher.Stopped += (sender, args) => deviceWatcher.Start();
        deviceWatcher.EnumerationCompleted += (sender, args) => deviceWatcher.Stop();

        deviceWatcher.Start();
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
