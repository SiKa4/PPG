﻿using IronPython.Hosting;
using System;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Foundation.Diagnostics;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Controls;
using static IronPython.Modules._ast;

internal class Program
{
    private const string HeartRateServiceId = "180d";
    private const string HeartRateCharacteristicId = "2a37";

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
        };

        string? deviceId = null;

        //python parser
        var engine = Python.CreateEngine();
        var scriptSource = engine.CreateScriptSourceFromFile("./parser.py");

        dynamic scope = engine.CreateScope();
        scriptSource.Execute(scope);
        dynamic myClass = scope.HRParser();
        ///

        Send($"disconnected: disconnected");

        deviceWatcher.Added += async (sender, device) =>
        {
            if (!device.Name.Contains(deviceName) || deviceId == device.Id) return;

            BluetoothLEDevice bluetoothLeDevice = await BluetoothLEDevice.FromIdAsync(device.Id);
            GattDeviceServicesResult result = await bluetoothLeDevice.GetGattServicesAsync();

            if (result == null && result.Status != GattCommunicationStatus.Success)
            {
                Send($"disconnected: result == null && result.Status != GattCommunicationStatus.Success");
                return;
            }

            var services = result.Services;
            var service = services.FirstOrDefault(svc => svc.Uuid.ToString("N").Substring(4, 4) == HeartRateServiceId);

            if (service == null)
            {
                Send($"disconnected: service == null");
                return;
            }

            var charactiristicResult = await service.GetCharacteristicsAsync();

            if (charactiristicResult.Status != GattCommunicationStatus.Success)
            {
                Send($"disconnected: charactiristicResult.Status != GattCommunicationStatus.Success");
                return;
            }

            var characteristic = charactiristicResult.Characteristics.FirstOrDefault(chr => chr.Uuid.ToString().Contains(HeartRateCharacteristicId));

            if (service == null || characteristic == null) 
            {
                Send($"disconnected: service == null || characteristic == null");
                return;
            } 

            GattCommunicationStatus status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);

            if (status != GattCommunicationStatus.Success)
            {
                Send($"disconnected: status != GattCommunicationStatus.Success");
                return;
            }

            deviceId = device.Id;

            var service_ppg = services.FirstOrDefault(svc => svc.Uuid.ToString().Contains(PPG_ID));

            var charactiristicResult_PPG = await service_ppg.GetCharacteristicsAsync();

            var characteristic_PPG_Read = charactiristicResult_PPG.Characteristics.FirstOrDefault(chr => chr.Uuid.ToString().Contains(PMD_DATA_UUID));

            if (characteristic_PPG_Read == null)
            {
                Send($"disconnected: characteristic_PPG_Read == null");
                return;
            }

            status =  await characteristic_PPG_Read.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);

            if(status != GattCommunicationStatus.Success)
            {
                Send($"disconnected: status != GattCommunicationStatus.Success");
                return;
            }

            Send($"connected: {device.Name}");

            var characteristic_PPG_Write = charactiristicResult_PPG.Characteristics.FirstOrDefault(chr => chr.Uuid.ToString().Contains(PMD_CONTROL));

            byte[] PPG_SETTING = new byte[] { 2, 1, 0, 1, 55, 0, 1, 1, 22, 0, 4, 1, 4 };
            await characteristic_PPG_Write.WriteValueAsync(PPG_SETTING.AsBuffer(), GattWriteOption.WriteWithoutResponse);

            int iterationsOfNotBeingOnHand = 4;

            bool isWearing = true;

            characteristic.ValueChanged += (gattCharacteristic, eventArgs) =>
            {
                var value = BitConverter.ToInt16(eventArgs.CharacteristicValue.ToArray().Reverse().ToArray(), 0);
                if (!isWearing) value = 0;
                Send($"hr: {value}");
            };

            async Task ReadPPG(GattValueChangedEventArgs eventArgs)
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
            }

            characteristic_PPG_Read.ValueChanged += async (gattCharacteristic, eventArgs) =>
            {
                ReadPPG(eventArgs);
                await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
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
