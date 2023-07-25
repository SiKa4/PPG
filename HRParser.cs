using System;
using System.Collections.Generic;

public class HRParser
{
    private object samplerate = 176;
    private Dictionary<string, object> intime = new Dictionary<string, object>();
    private Dictionary<string, object> graph_time = new Dictionary<string, object>();
    private object duration = 5000;
    private object firstTime = 0;
    private Dictionary<string, object> starttime = new Dictionary<string, object>();
    private Dictionary<string, object> layout_combined = new Dictionary<string, object>();
    private Dictionary<string, object> last_incom = new Dictionary<string, object>();
    private Dictionary<string, object> last_filter = new Dictionary<string, object>();
    private object timeconstant = 0.2;
    private Dictionary<string, object> dataobject = new Dictionary<string, object>();

    private List<object> createArray(int length, bool isRecutsion, params object[] args)
    {
        List<object> arr = new List<object>(new object[length]);
        int i = length;
        if (args.Length > 0)
        {
            while (i > 0)
            {
                if(isRecutsion && length - 1 - i >= 0)
                    arr[length - 1 - i] = createArray(Convert.ToInt32(args[0]), false, GetRange(args, 1, args.Length));
                i--;
            }
        }
        return arr;
    }

    public static T[] GetRange<T>(T[] source, int start, int end)
    {
        if (start < 0)
            throw new ArgumentOutOfRangeException(nameof(start), "Start index must be non-negative.");
        if (end >= source.Length)
            end = source.Length - 1;
        if (start > end)
            start = 0;

        int rangeLength = end - start + 1;
        T[] result = new T[rangeLength];

        for (int i = 0; i < rangeLength; i++)
        {
            result[i] = source[start + i];
        }

        return result;
    }

    private List<List<object>> createNestedList(int length, params object[] args)
    {
        List<List<object>> nestedList = new List<List<object>>(new List<object>[length]);
        for (int i = 0; i < length; i++)
        {
            nestedList[i] = createArray(Convert.ToInt32(args[0]),true ,GetRange(args, 1, args.Length));
        }
        return nestedList;
    }

    private List<List<object>> reslice(byte[] arr, object bits, object channels)
    {
        int offset = 0;
        int block = Convert.ToInt32(bits) / 2 * Convert.ToInt32(channels);
        if (block == 0) block = 1;
        int length = arr.Length / block;
        List<List<object>> f_array = createNestedList(length, channels);
        while (offset < length * block)
        {
            for (int a = 0; a < Convert.ToInt32(channels); a++)
            {
                byte[] miniArray = new byte[Convert.ToInt32(bits) / 2];
                Buffer.BlockCopy(arr, offset + a * Convert.ToInt32(bits) / 2, miniArray, 0, Convert.ToInt32(bits) / 2);
                f_array[Convert.ToInt32(offset / block)][a] = wordsToSignedInteger(miniArray, 2);
            }
            offset += block;
        }
        return f_array;
    }

    private Dictionary<string, object> rdp(Dictionary<string, object> data, double resolution)
    {
        double dmax = 0;
        double maxv = 0;
        int end_index = ((List<object>)data["x"]).Count - 1;
        double m = (Convert.ToDouble(((List<object>)data["y"])[0]) - Convert.ToDouble(((List<object>)data["y"])[end_index])) / (Convert.ToDouble(((List<object>)data["x"])[0]) - Convert.ToDouble(((List<object>)data["x"])[end_index]));
        double c = Convert.ToDouble(((List<object>)data["y"])[0]) - m * Convert.ToDouble(((List<object>)data["x"])[0]);
        for (int j = 1; j < end_index; j++)
        {
            double error = Math.Abs(Convert.ToDouble(((List<object>)data["y"])[j]) - m * Convert.ToDouble(((List<object>)data["x"])[j]) - c);
            if (error > dmax)
            {
                dmax = error;
                maxv = j;
            }
        }
        Dictionary<string, object> r1 = new Dictionary<string, object>();
        if (dmax > resolution)
        {
            end_index++;
            int cut = Convert.ToInt32(maxv + 1);
            r1 = rdp(new Dictionary<string, object>() { { "x", ((List<object>)data["x"]).GetRange(0, cut) }, { "y", ((List<object>)data["y"]).GetRange(0, cut) } }, resolution);
            Dictionary<string, object> r2 = rdp(new Dictionary<string, object>() { { "x", ((List<object>)data["x"]).GetRange(Convert.ToInt32(maxv), end_index - Convert.ToInt32(maxv)) }, { "y", ((List<object>)data["y"]).GetRange(Convert.ToInt32(maxv), end_index - Convert.ToInt32(maxv)) } }, resolution);
            ((List<object>)r1["x"]).AddRange((List<object>)r2["x"]);
            ((List<object>)r1["y"]).AddRange((List<object>)r2["y"]);
        }
        else
        {
            ((List<object>)r1["x"]).Add(((List<object>)data["x"])[0]);
            ((List<object>)r1["y"]).Add(((List<object>)data["y"])[0]);
        }
        return r1;
    }

    private List<object> chunkByte(byte sbytes)
    {
        List<object> n = new List<object>();
        int[] ts = { 1, 4, 16, 64 };
        int[] tg = { 3, 12, 48, 192 };
        for (int a = 0; a < 4; a++)
        {
            n.Add((sbytes & tg[a]) / ts[a]);
        }
        return n;
    }

    private List<List<object>> addDeltaFrame(List<List<object>> frame, List<List<object>> data_array)
    {
        try
        {
            int chans = frame[0].Count;
            for (int offset = 0; offset < frame.Count; offset++)
            {
                for (int ch = 0; ch < chans; ch++)
                {
                    data_array[ch].Add(Convert.ToDouble(data_array[ch][data_array[ch].Count - 1]) + Convert.ToDouble(frame[offset][ch]));
                }
            }
        }
        catch { }
        
        return data_array;
    }

    private List<object> chunkArray(byte[] arr)
    {
        List<object> newArray = new List<object>();
        for (int offset = 0; offset < arr.Length; offset++)
        {
            newArray.AddRange(chunkByte(arr[offset]));
        }
        return newArray;
    }

    private Dictionary<string, object> deltaFrameDescription(byte[] b, int channels)
    {
        int bits = 0;
        int number = 0;
        try { bits = b[0]; }
        catch (Exception e) { }

        try { number = b[1]; }
        catch (Exception e) { }
        
        return new Dictionary<string, object>() { { "bits", bits }, { "number", number }, { "bytes", number * bits / 8 * channels }, { "channels", channels } };
    }

    private int wordsToSignedInteger(byte[] words, int bitsPerWord)
    {
        int val = 0;
        int wordVal = int.Parse(Math.Round(Math.Pow(2, bitsPerWord)).ToString());
        for (int i = 0; i < words.Length; i++)
        {
            val += words[i] * int.Parse(Math.Round(Math.Pow(wordVal, i)).ToString());
        }
        int bits = words.Length * bitsPerWord;
        if (val > Math.Pow(2, bits - 1))
        {
            val -= int.Parse(Math.Round(Math.Pow(2, bits)).ToString());
        }
        return val;
    }

    private List<List<object>> getInitialSensorValues(byte[] a, int bytes)
    {
        List<List<object>> sensors = new List<List<object>>();
        int offset = 0;
        while (offset < a.Length)
        {
            List<object> sensor = new();
            sensor.Add(wordsToSignedInteger(GetRange(a, offset, offset + bytes), 8));
            sensors.Add(sensor);
            offset += bytes;
        }
        return sensors;
    }

    private List<List<object>> completeDeltaFrame(byte[] data, int numChan, int bytes)
    {
        int headerPointer = 10 + numChan * bytes;
        int framePointer = headerPointer + 2;
        List<List<object>> data_array = getInitialSensorValues(GetRange(data, 10, headerPointer), bytes);
        while (framePointer < data.Length)
        {
            Dictionary<string, object> deltaFrameDetails = deltaFrameDescription(GetRange(data, framePointer, framePointer), numChan);
            int nextHeaderPointer = framePointer + Convert.ToInt32(deltaFrameDetails["bytes"]);
            List<List<object>> frame = reslice(GetRange(data, framePointer, nextHeaderPointer), deltaFrameDetails["bits"], deltaFrameDetails["channels"]);
            data_array = addDeltaFrame(frame, data_array);
            headerPointer = nextHeaderPointer;
            framePointer = headerPointer + 2;
        }
        return data_array;
    }

    private List<double> fillTimeArray(string devicename, byte[] t, double dTime, int num, double step)
    {
        double startPacketTime = BitConverter.ToInt64(t, 0) / 1000000.0 - num * step;
        double computerTime = dTime - num * step;
        if (!starttime.ContainsKey(devicename))
        {
            DateTime x = DateTime.Parse("1/1/2000 00:00:00");
            DateTime y = DateTime.Parse("1/1/1970 00:00:00");
            double seconds = (Math.Abs((x - y).TotalSeconds * 1000) + startPacketTime) / 1000;
            DateTime startDate = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
            if (firstTime.Equals(0))
            {
                firstTime = computerTime;
            }
            starttime[devicename] = startPacketTime;
        }
        double streamTime = (double)starttime[devicename];
        List<double> a = new List<double>();
        for (int i = 0; i < num; i++)
        {
            a.Add(Math.Round((streamTime + step * i) * 1000) / 1000);
        }
        return a;
    }

    private List<double> hpFilter(List<double> array, ref double lastIn, ref double lastOut)
    {
        List<double> fArr = new List<double>(array.Count);
        double fraction = 1 + 1.0 / (Convert.ToDouble(samplerate) * Convert.ToDouble(timeconstant));
        for (int n = 0; n < array.Count; n++)
        {
            if(fArr.Count != 0)
                lastOut = fArr[n] = Math.Round((array[n] + lastOut - lastIn) / fraction);
            lastIn = array[n];
        }
        return fArr;
    }

    private void pushData(string device, List<object> ids, List<double> timeArray, List<List<double>> yValues)
    {
        if (!dataobject.ContainsKey(device))
        {
            dataobject[device] = new Dictionary<object, DataLists>();
        }

        for (int i = 0; i < ids.Count; i++)
        {
            object value = ids[i];
            if (!((Dictionary<object, DataLists>)dataobject[device]).ContainsKey(value))
            {
                ((Dictionary<object, DataLists>)dataobject[device])[value] = new DataLists();
            }

            ((Dictionary<object, DataLists>)dataobject[device])[value].X.AddRange(timeArray);
            ((Dictionary<object, DataLists>)dataobject[device])[value].Y.AddRange(yValues[i]);
        }
    }

    private class DataLists
    {
        public List<double> X { get; set; } = new List<double>();
        public List<double> Y { get; set; } = new List<double>();
    }


    private void pushDataTrace(string device, string name, double x, double y)
    {
        if (!dataobject.ContainsKey(device))
        {
            dataobject[device] = new Dictionary<string, DataLists>();
        }

        if (!((Dictionary<object, DataLists>)dataobject[device]).ContainsKey(name))
        {
            ((Dictionary<object, DataLists>)dataobject[device])[name] = new DataLists();
        }

        ((Dictionary<object, DataLists>)dataobject[device])[name].X.Add(x);
        ((Dictionary<object, DataLists>)dataobject[device])[name].Y.Add(y);
    }

    private int dataArrayLength(string deviceName, string name)
    {
        if (!dataobject.ContainsKey(deviceName) || !((Dictionary<object, object>)dataobject[deviceName]).ContainsKey(name))
        {
            ((Dictionary<object, object>)dataobject[deviceName])[name] = new Dictionary<string, List<double>>() { { "x", new List<double>() }, { "y", new List<double>() } };
            return 0;
        }
        else
        {
            return ((List<double>)((Dictionary<string, List<double>>)((Dictionary<object, object>)dataobject[deviceName])[name])["x"]).Count;
        }
    }

    private int parseHeartRate(byte[] buffer)
    {
        return buffer[1];
    }

    public Dictionary<string, object> parsePpg(byte[] buffer)
    {
        byte[] data = buffer;

        int dataType = data[0];
        if (dataType != 1)
        {
            return null;
        }

        long dataTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        string deviceName = "test";
        double deviceTime = BitConverter.ToInt64(GetRange(data, 1, 9), 0) / 1000000.0;
        if (!layout_combined.ContainsKey(deviceName))
        {
            layout_combined[deviceName] = new Dictionary<string, object>
            {
                { "autosize", false },
                { "width", 400 },
                { "height", 150 },
                { "margin", new Dictionary<string, object>{{ "l", 0 }, { "r", 10 }, { "b", 0 }, { "t", 0 }, { "pad", 0 }} },
                { "xaxis", new Dictionary<string, object>{{ "showticklabels", false }, { "range", new List<double> { 0, 1 } }} },
                { "yaxis", new Dictionary<string, object>{{ "showticklabels", false }, { "range", new List<double> { 0, 1 } }} },
                { "yaxis2", new Dictionary<string, object>{{ "showticklabels", false }, { "overlaying", "y" }, { "side", "right" }} },
                { "legend", new Dictionary<string, object>{{ "orientation", "h" }, { "font", new Dictionary<string, object>{{ "family", "sans-serif" }, { "size", 8 }}}} }
            };
        }

        if (!intime.ContainsKey(deviceName))
        {
            intime[deviceName] = deviceTime;
        }
        if (!graph_time.ContainsKey(deviceName))
        {
            graph_time[deviceName] = 0;
        }
        if (Convert.ToDouble(graph_time[deviceName]) > (Convert.ToDouble(intime[deviceName]) + Convert.ToDouble(duration)))
        {
            intime[deviceName] = graph_time[deviceName];
            ((Dictionary<string, object>)layout_combined[deviceName])["xaxis"] = new Dictionary<string, object> { { "range", new List<double> { Convert.ToDouble(intime[deviceName]) - 200, Convert.ToDouble(intime[deviceName]) + Convert.ToDouble(duration) } } };
        }

        // PPG
        List<double> ppgSum = new List<double>();
        if (!last_incom.ContainsKey(deviceName))
        {
            last_incom[deviceName] = 0;
            last_filter[deviceName] = 0;
        }

        List<List<object>> newPpg = completeDeltaFrame(data, 4, 3);
        int npoints = newPpg[0].Count;
        for (int i = 0; i < npoints; i++)
        {
            ppgSum.Add(Convert.ToDouble(newPpg[0][i]) + Convert.ToDouble(newPpg[1][i]) + Convert.ToDouble(newPpg[2][i]));
        }

        List<double> ppgTime = fillTimeArray(deviceName, GetRange(data, 1, 9), dataTime, npoints, 1000.0 / Convert.ToDouble(samplerate));
        pushData(deviceName, new List<object> { "ppg_sum_2" }, ppgTime, new List<List<double>> { ppgSum });

        var last_incom_copy = double.Parse(last_incom[deviceName].ToString());
        var last_filter_copy = double.Parse(last_filter[deviceName].ToString());
        List<double> ppgSumFilter = hpFilter(ppgSum, ref last_incom_copy, ref last_filter_copy);
        if(ppgSumFilter.Count != 0 && ppgSumFilter.Count - 1 >= 0)
            last_filter[deviceName] = ppgSumFilter[ppgSumFilter.Count - 1];
        else
            last_filter[deviceName] = 0;

        if (ppgSum.Count != 0 && ppgSum.Count - 1 >= 0)
            last_incom[deviceName] = ppgSum[ppgSum.Count - 1];
        else
            last_incom[deviceName] = 0;
        graph_time[deviceName] = deviceTime;

        pushData(deviceName, new List<object> { "ppg_sum" }, ppgTime, new List<List<double>> { ppgSumFilter });
        return new Dictionary<string, object> { { "y", ppgSumFilter }, { "x", ppgTime } };
    }
}

