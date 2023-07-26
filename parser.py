import datetime
import struct
import time


class HRParser:
    def __init__(self):
        self.samplerate = 176  # PPG maximum in SDK 28Hz, 44Hz, 135Hz, 176Hz
        self.intime = {}
        self.graph_time = {}
        self.duration = 5000  # ms for graph
        self.firstTime = 0
        self.starttime = {}
        self.layout_combined = {}
        self.last_incom = {}
        self.last_filter = {}
        self.timeconstant = 0.2  # timeconstant for highpass in s
        self.dataobject = {}

    def create_array(self, length, *args):
        arr = [None] * (length or 0)
        i = length
        if args and len(args) > 0:
            while i:
                arr[length - 1 - i] = self.create_array(*args)
                i -= 1
        return arr

    def reslice(self, arr, bits, channels):
        offset = 0
        block = bits / 2 * channels
        length = int(len(arr) / block)
        f_array = self.create_array(length, channels)
        while offset < length * block:
            for a in range(channels):
                mini_array = arr[int(offset + a * bits / 2): int(offset + (a + 1) * bits / 2)]
                f_array[round(offset // block)][a] = self.words_to_signed_integer(mini_array, 2)
            offset += block
        return f_array

    def rdp(self, data, resolution):
        dmax = 0
        maxv = 0
        end_index = len(data['x']) - 1
        m = (data['y'][0] - data['y'][end_index]) / (data['x'][0] - data['x'][end_index])
        c = data['y'][0] - m * data['x'][0]
        for j in range(1, end_index):
            error = abs(data['y'][j] - m * data['x'][j] - c)
            if error > dmax:
                dmax = error
                maxv = j
        r1 = {'x': [], 'y': []}
        if dmax > resolution:
            end_index += 1
            cut = maxv + 1
            r1 = self.rdp({'x': data['x'][:cut], 'y': data['y'][:cut]}, resolution)
            r2 = self.rdp({'x': data['x'][maxv:end_index], 'y': data['y'][maxv:end_index]}, resolution)
            r1['x'].extend(r2['x'])
            r1['y'].extend(r2['y'])
        else:
            r1['x'].append(data['x'][0])
            r1['y'].append(data['y'][0])
        return r1

    def chunk_byte(self, sbyte):
        n = []
        ts = [1, 4, 16, 64]
        tg = [3, 12, 48, 192]
        for a in range(4):
            n.append((sbyte & tg[a]) / ts[a])
        return n


    def add_deltaframe(self, frame, data_array):
        chans = len(frame[0])
        for offset in range(len(frame)):
            for ch in range(chans):
                data_array[ch].append(data_array[ch][-1] + frame[offset][ch])
        return data_array


    def chunk_array(self, arr):
        arr = bytearray(arr)
        offset = 0
        NewArray = []
        while offset < len(arr):
            NewArray += self.chunk_byte(arr[offset])
            offset += 1
        return NewArray


    def delta_frame_description(self, b, channels):
        a = bytearray(b)
        bits = a[0]
        number = a[1]
        return {'bits': bits, 'number': number, 'bytes': int(number * bits / 8 * channels), 'channels': channels}

    def words_to_signed_integer(self, words, bits_per_word):
        val = 0
        word_val = 2 ** bits_per_word
        for i in range(len(words)):
            val += words[i] * word_val ** i
        bits = len(words) * bits_per_word
        if val > 2 ** (bits - 1):
            val = val - 2 ** bits
        return val


    def get_initial_sensor_values(self, a, bytes):
        a = bytearray(a)
        sensors = [[0] for i in range(len(a) // bytes)]
        offset = 0
        while offset < len(a):
            sensors[offset // bytes][0] = self.words_to_signed_integer(a[offset:offset+bytes], 8)
            offset += bytes
        return sensors


    def complete_delta_frame(self, data, num_chan, bytes):
        header_pointer = 10 + num_chan * bytes
        frame_pointer = header_pointer + 2
        data_array = self.get_initial_sensor_values(data[10:header_pointer], bytes)
        while frame_pointer < len(data):
            delta_frame_details = self.delta_frame_description(data[header_pointer:frame_pointer], num_chan)
            next_header_pointer = frame_pointer + delta_frame_details['bytes']
            frame = self.reslice(self.chunk_array(data[frame_pointer:next_header_pointer]), delta_frame_details['bits'], delta_frame_details['channels'])
            data_array = self.add_deltaframe(frame, data_array)
            header_pointer = next_header_pointer
            frame_pointer = header_pointer + 2
        return data_array

    def fill_time_array(self, devicename, t, dTime, num, step):
        start_packet_time = struct.unpack("<q", t)[0] / 1000000 - num * step
        computer_time = dTime - num * step
        if devicename not in self.starttime:
            x = datetime.datetime.strptime("1/1/2000 00:00:00", "%m/%d/%Y %H:%M:%S")
            y = datetime.datetime.strptime("1/1/1970 00:00:00", "%m/%d/%Y %H:%M:%S")
            seconds = (abs((x - y).total_seconds() * 1000) + start_packet_time) / 1000
            startdate = datetime.datetime.fromtimestamp(seconds)
            if self.firstTime == 0:
                self.firstTime = computer_time
            self.starttime[devicename] = start_packet_time  # deviceEPOCH - firstTime
        stream_time = self.starttime[devicename]  # + starttime[devicename] # try to switch to real time recording of time.
        a = [0] * num
        for i in range(num):
            a[i] = round((stream_time + step * i) * 1000) / 1000
        return a

    def hp_filter(self, array, last_in, last_out):
        f_arr = [0] * len(array)
        fraction = (1 + 1 / (self.samplerate * self.timeconstant))
        for n, value in enumerate(array):
            last_out = f_arr[n] = round((value + last_out - last_in) / fraction)
            last_in = value
        return f_arr

    def push_data(self, device, ids, timearray, y_values):
        if device not in self.dataobject:
            self.dataobject[device] = {}
        for value in ids:
            if value not in self.dataobject[device]:
                self.dataobject[device][value] = {'x': [], 'y': []}
            self.dataobject[device][value]['x'].extend(timearray)
            self.dataobject[device][value]['y'].extend(y_values[ids.index(value)])

    def push_data_trace(self, device, name, x, y):
        if device not in self.dataobject:
            self.dataobject[device] = {}
        self.dataobject[device][name]['x'].append(x)
        self.dataobject[device][name]['y'].append(y)

    def data_array_length(self, devicename, name):
        if devicename not in self.dataobject or name not in self.dataobject[devicename]:
            self.dataobject[devicename][name] = {'x': [], 'y': []}
            return 0
        else:
            return len(self.dataobject[devicename][name]['x'])


    def parse_heart_rate(self, buffer):
        return buffer[1]


    def parse_ppg(self, buffer):
        data = buffer

        data_type = int.from_bytes(data[0:1], byteorder='little')
        if data_type != 1:
            return

        data_time = int(time.time() * 1000)
        device_name = "test"
        device_time = int.from_bytes(data[1:9], byteorder='little', signed=True) / 1000000
        if device_name not in self.layout_combined:
            self.layout_combined[device_name] = {
                "autosize": False,
                "width": 400,
                "height": 150,
                "margin": {"l": 0, "r": 10, "b": 0, "t": 0, "pad": 0},
                "xaxis": {"showticklabels": False, "range": [0, 1]},
                "yaxis": {"showticklabels": False, "range": [0, 1]},
                "yaxis2": {"showticklabels": False, "overlaying": "y", "side": "right"},
                "legend": {"orientation": "h", "font": {"family": "sans-serif", "size": 8}}
            }

        if device_name not in self.intime:
            self.intime[device_name] = device_time
        if device_name not in self.graph_time.keys():
            self.graph_time[device_name] = 0
        if self.graph_time[device_name] > (self.intime[device_name] + self.duration):
            self.intime[device_name] = self.graph_time[device_name]
            self.layout_combined[device_name]["xaxis"]["range"] = [self.intime[device_name] - 200, self.intime[device_name] + self.duration]

        # PPG
        ppg_sum = []
        if device_name not in self.last_incom:
            self.last_incom[device_name] = 0
            self.last_filter[device_name] = 0
        new_ppg = self.complete_delta_frame(data, 4, 3)  # 4 channels
        npoints = len(new_ppg[0])
        for i in range(npoints):
            ppg_sum.append(new_ppg[0][i] + new_ppg[1][i] + new_ppg[2][i])
        
        ppg_time = self.fill_time_array(device_name, data[1:9], data_time, npoints, 1000 / self.samplerate)
        self.push_data(device_name, ["ppg_sum_2"], ppg_time, [ppg_sum])
        ppg_sum_filter = self.hp_filter(ppg_sum, self.last_incom[device_name], self.last_filter[device_name])
        self.last_filter[device_name] = ppg_sum_filter[-1]
        self.last_incom[device_name] = ppg_sum[-1]
        self.graph_time[device_name] = device_time

        self.push_data(device_name, ["ppg_sum"], ppg_time, [ppg_sum_filter])
        return {"y": ppg_sum_filter, "x": ppg_time}



