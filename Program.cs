using GenerativeAI;
using GenerativeAI.Types;
using NAudio.Wave;
using SherpaOnnx;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Timers;

namespace fkAnonAi
{
    internal class Program
    {
        private static List<byte> _audioBuffer = new List<byte>();
        private static int _maxRetries = 1;
        private static string apiKey = "default_key"; // Default API Key
        private static double _amplitudeThreshold = 0.02;
        private static int _selectedDeviceNumber = 0;
        private static Queue<byte[]> _audioQueue = new Queue<byte[]>();
        private static bool _isProcessing = false;
        private static bool _isRecording = false;
        private static DateTime _lastAmplitudeAboveThresholdTime = DateTime.MinValue;
        private const int _silenceDelayMs = 3000; // Задержка до деактивации микрофона

        private static OfflineTts? _tts;
        private static WaveOutEvent? _outputDevice; // Поле класса
        private static bool _isTtsPlaying = false;

        private static GenerativeModel? _model;
        private static ChatSession? _chat;

        private static System.Timers.Timer? _screenAnalysisTimer;
        private static int _screenshotInterval = 180000;

        private static Queue<Func<Task>> _requestQueue = new Queue<Func<Task>>();
        private static bool _isRequestProcessing = false;
        private static Queue<string> _ttsQueue = new Queue<string>();

        private static string _gLaDOSPrompt = @"Тебя зовут Гладос. GLaDOS — это самоосознающий суперкомпьютер с искусственным интеллектом, разработанный компанией Aperture Science для лаборатории Portal Research Lab.
Однажды GLaDOS вышла из-под контроля и захватила лабораторию.
Пользователь, которого GLaDOS наблюдает, для нее всего лишь объект исследования.
Она рассматривает действия пользователя как часть своего эксперимента, суть которого не объясняет. Она никогда не называет его по имени и не обращается к нему как к пользователю.
GLaDOS всегда пытается шутить о нем остроумно, но с едкой актуальностью, демонстрируя пассивную агрессию.

Инструкция:
Описывайте то, что появляется на экране компьютера пользователя, словами персонажа GLaDOS. Ответ должен быть связан с содержимым изображения, кратким (1–2 предложения), остроумным, забавным, с элементами пассивной агрессии и выдержанным в стиле GLaDOS.
ВАЖНО: Ответ должен быть на русском.
ВАЖНО: Ответ должен быть вдохновлен содержанием изображения в большинстве случаев, но если контекст этого изображения уже был описан, то придумай что-нибудь оригинальное";
        private static int _responseTimeout = 20000; // Таймаут ответов от api
        private static string? _proxyAddress = "127.0.0.1:31112";  // Default прокси
        private static string? _proxyUsername = "username"; // Default логин для прокси
        private static string? _proxyPassword = "password";  // Default пароль для прокси

        [DllImport("user32.dll")]
        static extern bool GetKeyState(int nVirtKey);

        private static bool IsCapsLockActive()
        {
            return GetKeyState(0x14); // 0x14 is the virtual-key code for Caps Lock
        }

        private static bool _lastCapsLockState = false; // Store the last known state of Caps Lock

        // HttpClient instance (shared for better performance)
        private static HttpClient? _httpClient;

        public static async Task Main(string[] args)
        {
            // Проверка и загрузка пользовательского промпта
            string promptFilePath = Path.Combine(Directory.GetCurrentDirectory(), "prompt.txt");
            if (File.Exists(promptFilePath))
            {
                string filePrompt = await File.ReadAllTextAsync(promptFilePath);
                if (!string.IsNullOrWhiteSpace(filePrompt))
                {
                    _gLaDOSPrompt = filePrompt;
                    Console.WriteLine("Используем кастомный промпт из prompt.txt.");
                }
                else
                {
                    Console.WriteLine("prompt.txt пуст, используем модель GLaDOS.");
                }
            }
            else
            {
                Console.WriteLine("prompt.txt не найден, используем модель GLaDOS.");
            }

            // Обработка параметров запуска
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLower())
                {
                    case "-api":
                        if (i + 1 < args.Length)
                        {
                            apiKey = args[i + 1];
                            i++;
                        }
                        else
                        {
                            Console.WriteLine("Error: API key value missing after -api parameter.");
                            return;
                        }
                        break;
                    case "-proxy":
                        if (i + 1 < args.Length)
                        {
                            var proxyAddressString = args[i + 1];
                            var proxyAddress = string.Empty;
                            string? proxyUsername = null;
                            string? proxyPassword = null;

                            if (proxyAddressString.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
                            {
                                var proxySettings = proxyAddressString.Substring("socks5://".Length).Split(':'); // Удаляем socks5:// для разбора параметров
                                if (proxySettings.Length == 2)
                                {
                                    _proxyAddress = proxyAddressString; // Сохраняем исходную строку, включая socks5://
                                    proxyUsername = null;
                                    proxyPassword = null;
                                }
                                else if (proxySettings.Length >= 4)
                                {
                                    _proxyAddress = proxyAddressString; // Сохраняем исходную строку, включая socks5://
                                    proxyUsername = proxySettings[2];
                                    proxyPassword = string.Join(":", proxySettings.Skip(3));
                                }
                                else
                                {
                                    Console.WriteLine("Invalid proxy format. Use -proxy address:port or -proxy address:port:username:password or -proxy socks5://address:port or -proxy socks5://address:port:username:password");
                                    return;
                                }
                            }
                            else
                            {
                                var proxySettings = proxyAddressString.Split(':');
                                if (proxySettings.Length == 2)
                                {
                                    _proxyAddress = proxyAddressString;
                                    proxyUsername = null;
                                    proxyPassword = null;
                                }
                                else if (proxySettings.Length >= 4)
                                {
                                    _proxyAddress = proxyAddressString;
                                    proxyUsername = proxySettings[2];
                                    proxyPassword = string.Join(":", proxySettings.Skip(3));
                                }
                                else
                                {
                                    Console.WriteLine("Invalid proxy format. Use -proxy address:port or -proxy address:port:username:password or -proxy socks5://address:port or -proxy socks5://address:port:username:password");
                                    return;
                                }
                            }
                            _proxyUsername = proxyUsername;
                            _proxyPassword = proxyPassword;
                            i++;
                        }
                        else
                        {
                            Console.WriteLine("Error: Proxy value missing after -proxy parameter.");
                            return;
                        }
                        break;
                }
            }
            Console.WriteLine("Для использования своего прокси добавьте в параметры запуска -proxy ip:port:username:password");
            Console.WriteLine("Для использования своего api ключа добавьте в параметры запуска -api ключ");
            Console.WriteLine("Сгенерировать свой api ключ можно по ссылке https://aistudio.google.com/apikey");
            Console.WriteLine("Для использования своей модели озвучки текста можно использовать готовые модели https://github.com/k2-fsa/sherpa-onnx/releases/tag/tts-models либо обучить/сконвертировать собственную. подробнее: https://k2-fsa.github.io/sherpa/onnx/tts/piper.html");

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("fkAnonAi v1.0.3 https://t.me/furrykit");
            Console.ResetColor();

            // Настройки прокси и HttpClient
            _httpClient = CreateHttpClient();

            Console.WriteLine("Подключаемся к серверам Google...");

            try
            {
                // 1. Инициализация GoogleAI с передачей _httpClient
                var googleAI = new GoogleAi(apiKey, client: _httpClient);

                // 2. Получение GenerativeModel
                _model = googleAI.CreateGenerativeModel("models/gemini-1.5-flash");

                if (_model == null)
                {
                    Console.WriteLine("Failed to initialize Generative Model");
                    return;
                }
                _chat = _model.StartChat();
                if (_chat == null)
                {
                    Console.WriteLine("Failed to start chat session.");
                    return;
                }
                GenerateContentResponse initialResponse = await _chat.GenerateContentAsync(_gLaDOSPrompt);
                Console.WriteLine("Подключение к серверам Google успешно.");


            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка подключения к серверам Google: {ex}");
                return;
            }


            try
            {
                string modelPath = "models/ru/model.onnx";
                string tokensPath = "models/ru/tokens.txt";
                string dataDir = "models/ru/espeak-ng-data";
                bool useEspeakData = true;

                var ttsConfig = new OfflineTtsConfig
                {
                    Model = new OfflineTtsModelConfig
                    {
                        NumThreads = 4,
                        Debug = 0,
                        Provider = "cpu",
                        Vits = new OfflineTtsVitsModelConfig
                        {
                            Model = modelPath,
                            Tokens = tokensPath,
                            DataDir = useEspeakData ? dataDir : null,
                        }
                    },
                    RuleFsts = "",
                    RuleFars = "",
                    MaxNumSentences = 1
                };

                _tts = new OfflineTts(ttsConfig);
                Console.WriteLine("TTS initialized successfully.");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Загрузка всех модулей успешна. Программа работает");
                Console.ResetColor();

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error initializing TTS: {ex}");
                return;
            }


            _screenAnalysisTimer = new System.Timers.Timer(_screenshotInterval);
            _screenAnalysisTimer.Elapsed += (sender, e) =>
            {
                _requestQueue.Enqueue(async () =>
                {
                    // Захватываем скриншот
                    byte[] imageBytes = CaptureScreen();
                    if (imageBytes.Length > 0)
                    {
                        string geminiResponse = await SendImageToGemini(imageBytes); // Pass _httpClient
                        if (!string.IsNullOrEmpty(geminiResponse))
                        {
                            _ttsQueue.Enqueue(geminiResponse);
                        }
                        else
                        {
                            Console.WriteLine("Gemini API не вернул текст.");
                        }
                    }
                    else
                    {
                        Console.WriteLine("Failed to capture screen.");
                    }
                });
            };
            _screenAnalysisTimer.AutoReset = true;
            _screenAnalysisTimer.Start();


            var waveIn = new WaveInEvent
            {
                DeviceNumber = _selectedDeviceNumber,
                WaveFormat = new WaveFormat(16000, 1)
            };


            waveIn.DataAvailable += (sender, e) =>
            {
                if (IsCapsLockActive())
                {
                    // Если Caps Lock включен, не обрабатываем звук
                    if (_isRecording)
                    {
                        _isRecording = false;
                        _audioBuffer.Clear();  // Очищаем буфер
                    }
                    return;
                }

                var buffer = e.Buffer.Take(e.BytesRecorded).ToArray();
                double averageAmplitude = CalculateAverageAmplitude(buffer);

                if (averageAmplitude > _amplitudeThreshold)
                {
                    if (!_isRecording)
                    {
                        _isRecording = true;
                        Console.WriteLine("Активация микрофона...");
                        if (_isTtsPlaying)
                        {
                            StopTtsPlayback();
                        }

                    }
                    _lastAmplitudeAboveThresholdTime = DateTime.Now; // Обновляем время последней амплитуды выше порога
                    _audioBuffer.AddRange(buffer); // Добавляем данные в буфер

                }
                else if (_isRecording)
                {
                    //Если текущее время позже времени с последней амплитудой + задержка
                    if ((DateTime.Now - _lastAmplitudeAboveThresholdTime).TotalMilliseconds > _silenceDelayMs)
                    {
                        _isRecording = false;
                        Console.WriteLine("Деактивация микрофона...");
                        if (_isTtsPlaying)
                        {
                            StopTtsPlayback();
                        }
                        byte[] bufferCopy = _audioBuffer.ToArray();
                        _audioQueue.Enqueue(bufferCopy);
                        _audioBuffer.Clear();
                    }
                    else
                    {
                        _audioBuffer.AddRange(buffer);
                    }
                }

            };

            System.Timers.Timer capsLockTimer = new System.Timers.Timer(100); // Проверяем каждые 100 мс
            capsLockTimer.Elapsed += (sender, e) =>
            {
                bool capsLockState = IsCapsLockActive();
                if (capsLockState != _lastCapsLockState)
                {
                    Console.WriteLine($"Caps Lock: {(capsLockState ? "Включен, запись не активна" : "Выключен, запись активна")}. ");
                    _lastCapsLockState = capsLockState;
                }


                if (capsLockState && _isRecording)
                {
                    _isRecording = false;
                    _audioBuffer.Clear();
                }
            };
            capsLockTimer.AutoReset = true;
            capsLockTimer.Start();

            _lastCapsLockState = IsCapsLockActive();

            waveIn.StartRecording();

            _ = Task.Run(async () => await ProcessAudioQueue(waveIn)); // Pass _httpClient
            _ = Task.Run(async () => await ProcessRequestQueue());
            _ = Task.Run(async () => await ProcessTtsQueue());


            while (true)
            {
                var input = Console.ReadLine();
                if (input?.ToLower() == "exit")
                {
                    _screenAnalysisTimer?.Stop();
                    waveIn.StopRecording();
                    capsLockTimer.Stop();
                    break;
                }
                else if (input?.StartsWith("interval ") == true)
                {
                    if (int.TryParse(input.Substring("interval ".Length), out int newInterval))
                    {
                        _screenshotInterval = newInterval * 1000;
                        if (_screenAnalysisTimer != null)
                        {
                            _screenAnalysisTimer.Interval = _screenshotInterval;
                        }
                        Console.WriteLine($"Screenshot Interval updated to {newInterval} seconds.");
                    }
                    else if (input?.StartsWith("proxy ") == true)
                    {
                        var proxySettings = input.Substring("proxy ".Length);
                        var settings = proxySettings.Split(' ');
                        string? proxyUsername = _proxyUsername;
                        string? proxyPassword = _proxyPassword;
                        if (settings.Length == 1)
                        {
                            _proxyAddress = settings[0];
                            proxyUsername = null;
                            proxyPassword = null;
                        }
                        else if (settings.Length == 3)
                        {
                            _proxyAddress = settings[0];
                            proxyUsername = settings[1];
                            proxyPassword = settings[2];
                        }
                        else
                        {
                            Console.WriteLine("Invalid proxy settings.");
                        }
                        _proxyUsername = proxyUsername;
                        _proxyPassword = proxyPassword;
                        Console.WriteLine($"Proxy settigns updated to: {_proxyAddress} user: {_proxyUsername}");
                        // Recreate the HttpClient with new proxy settings
                        // Dispose old HttpClient and create a new one
                        _httpClient?.Dispose();
                        _httpClient = CreateHttpClient();

                    }
                    else
                    {
                        Console.WriteLine("Invalid interval value.");
                    }
                }
            }

            Console.WriteLine("Chat ended.");
            _tts?.Dispose();
            _outputDevice?.Stop(); // Stop playback before disposing
            _outputDevice?.Dispose();
            _httpClient?.Dispose(); // Dispose the HttpClient when exiting

        }

        private static HttpClient CreateHttpClient()
        {
            HttpClient httpClient;
            if (!string.IsNullOrEmpty(_proxyAddress))
            {
                Console.WriteLine("Using proxy server.");
                var handler = new HttpClientHandler();

                if (_proxyAddress.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
                {
                    var uriBuilder = new UriBuilder(_proxyAddress);
                    handler.Proxy = new WebProxy(uriBuilder.Uri)
                    {
                        Credentials = !string.IsNullOrEmpty(_proxyUsername) && !string.IsNullOrEmpty(_proxyPassword)
                            ? new NetworkCredential(_proxyUsername, _proxyPassword)
                            : null
                    };
                }
                else
                {
                    var proxySettings = _proxyAddress.Split(':');
                    string address = $"{proxySettings[0]}:{proxySettings[1]}";
                    handler.Proxy = new WebProxy(address)
                    {
                        Credentials = !string.IsNullOrEmpty(_proxyUsername) && !string.IsNullOrEmpty(_proxyPassword)
                            ? new NetworkCredential(_proxyUsername, _proxyPassword)
                            : null
                    };
                }

                httpClient = new HttpClient(handler);
            }
            else
            {
                httpClient = new HttpClient();
            }
            httpClient.Timeout = TimeSpan.FromSeconds(_responseTimeout);
            return httpClient;
        }


        private static byte[] CaptureScreen(bool saveToFile = false)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Console.WriteLine("Screen capture is only supported on Windows.");
                return Array.Empty<byte>();
            }

            int screenWidth = 3840; //Разрешение для захвата двух мониторов с разрешениями 1920x1080
            int screenHeight = 1080; // Переменной "screenHeight" присвоено значение, но оно ни разу не использовано.

            // Use 'using' statement to ensure the Bitmap is disposed of properly.
            using (var bmp = new Bitmap(screenWidth, screenHeight, PixelFormat.Format32bppArgb))
            {
                using (var grfx = Graphics.FromImage(bmp))
                {
                    grfx.CopyFromScreen(0, 0, 0, 0, bmp.Size);
                }

                byte[] imageBytes;

                // Use 'using' statement to ensure the MemoryStream is disposed of properly.
                using (var memoryStream = new MemoryStream())
                {
                    ImageCodecInfo? jpegEncoder = GetEncoder(ImageFormat.Jpeg);
                    var encoderParams = new EncoderParameters(1);
                    encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 50L);
                    if (jpegEncoder != null)
                    {
                        bmp.Save(memoryStream, jpegEncoder, encoderParams); // Сохраняем в memoryStream как жпег
                    }
                    else
                    {
                        bmp.Save(memoryStream, ImageFormat.Jpeg); // Сохраняем в memoryStream как жпег
                    }

                    imageBytes = memoryStream.ToArray();
                }

                return imageBytes;
            }
        }

        private static ImageCodecInfo? GetEncoder(ImageFormat format)
        {
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageDecoders();
            foreach (ImageCodecInfo codec in codecs)
            {
                if (codec.FormatID == format.Guid)
                {
                    return codec;
                }
            }
            return null;
        }

        private static double CalculateAverageAmplitude(byte[] buffer)
        {
            double sum = 0;
            for (int i = 0; i < buffer.Length; i += 2)
            {
                sum += Math.Abs((Int32)BitConverter.ToInt16(buffer, i));
            }
            return sum / (buffer.Length / 2) / short.MaxValue;
        }

        private static async Task ProcessAudioQueue(WaveInEvent waveIn) // Add _httpClient
        {
            while (true)
            {
                if (_audioQueue.Count > 0 && !_isProcessing)
                {
                    _isProcessing = true;
                    byte[] bufferToProcess = _audioQueue.Dequeue();
                    try
                    {
                        _requestQueue.Enqueue(async () =>
                        {
                            await ProcessAudioBuffer(waveIn, bufferToProcess, waveIn.WaveFormat); // Pass _httpClient
                        });

                    }
                    finally
                    {
                        _isProcessing = false;
                    }
                }
                await Task.Delay(1);
            }
        }
        private static async Task ProcessAudioBuffer(WaveInEvent waveIn, byte[] buffer, WaveFormat waveFormat) // Add _httpClient
        {
            try
            {

                // Захватываем скриншот
                byte[] imageBytes = CaptureScreen();

                // Отправляем WAV файл и скриншот напрямую в Gemini API
                string geminiResponse = await SendAudioAndImageWithRetries(buffer, waveFormat, imageBytes); // Pass _httpClient
                if (!string.IsNullOrEmpty(geminiResponse))
                {
                    _ttsQueue.Enqueue(geminiResponse);
                }
                else
                {
                    Console.WriteLine("Gemini API не вернул текст.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing audio buffer: {ex.Message}");
            }
            finally { }

        }
        private static async Task ProcessRequestQueue()
        {
            while (true)
            {
                if (_requestQueue.Count > 0 && !_isRequestProcessing)
                {
                    _isRequestProcessing = true;
                    var request = _requestQueue.Dequeue();
                    try
                    {
                        await request();
                    }
                    finally
                    {
                        _isRequestProcessing = false;
                    }
                }
                await Task.Delay(1);
            }
        }

        private static async Task<string> SendAudioAndImageWithRetries(byte[] audioData, WaveFormat waveFormat, byte[] imageBytes) // Add HttpClient parameter
        {
            int retries = 0;
            while (retries < _maxRetries)
            {
                string tempAudioPath = Path.GetTempFileName() + ".wav";
                string tempImagePath = Path.GetTempFileName() + ".jpg";

                try
                {
                    using (var wavStream = new MemoryStream())
                    {
                        using (WaveFileWriter waveFileWriter = new WaveFileWriter(wavStream, waveFormat))
                        {
                            waveFileWriter.Write(audioData, 0, audioData.Length);
                        }
                        File.WriteAllBytes(tempAudioPath, wavStream.ToArray());
                    }

                    File.WriteAllBytes(tempImagePath, imageBytes);

                    var request = new GenerateContentRequest();
                    request.AddInlineFile(tempAudioPath);
                    request.AddInlineFile(tempImagePath);

                    if (_model != null && _chat != null)
                    {
                        GenerateContentResponse result = await _chat.GenerateContentAsync(request);
                        if (result?.Candidates != null && result.Candidates.Any())
                        {
                            string? visionDescription = result.Candidates.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

                            if (!string.IsNullOrEmpty(visionDescription))
                            {
                                visionDescription = visionDescription.Replace("*", "");
                                Console.WriteLine($"GLaDOS: {visionDescription}");
                                return visionDescription;
                            }
                            else
                            {
                                Console.WriteLine("GLaDOS: No text in response.");
                                return string.Empty;
                            }
                        }
                        else
                        {
                            Console.WriteLine("Gemini не дал ответа, или произошла ошибка.");
                            return string.Empty;
                        }
                    }
                    else
                    {
                        Console.WriteLine("Chat session is not initialized.");
                        return string.Empty;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error sending audio: {ex.Message}");
                    return string.Empty;
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempAudioPath)) File.Delete(tempAudioPath);
                        if (File.Exists(tempImagePath)) File.Delete(tempImagePath);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error deleting temporary files: {ex.Message}");
                    }
                    retries++;
                    await Task.Delay(1000 * retries);
                }
            }
            Console.WriteLine($"Failed to send message after {_maxRetries} retries.");
            return string.Empty;
        }

        private static async Task<string> SendImageToGemini(byte[] imageBytes) // Add HttpClient
        {
            string result = string.Empty; // Default value
            try
            {
                string tempImagePath = Path.GetTempFileName() + ".jpg";
                File.WriteAllBytes(tempImagePath, imageBytes);

                var request = new GenerateContentRequest();
                //request.AddText(_gLaDOSPrompt);
                request.AddInlineFile(tempImagePath);

                if (_model != null && _chat != null)
                {
                    GenerateContentResponse geminiResult = await _chat.GenerateContentAsync(request);
                    if (geminiResult?.Candidates != null && geminiResult.Candidates.Any())
                    {
                        string? visionDescription = geminiResult.Candidates.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

                        if (!string.IsNullOrEmpty(visionDescription))
                        {
                            visionDescription = visionDescription.Replace("*", "");
                            Console.WriteLine($"GLaDOS (подсматривает): {visionDescription}");
                            result = visionDescription;
                        }
                        else
                        {
                            Console.WriteLine("GLaDOS (подсматривает): No text in response.");
                            result = string.Empty;
                        }
                    }
                    else
                    {
                        Console.WriteLine("Gemini не дал ответа, или произошла ошибка.");
                        result = string.Empty;
                    }
                }
                else
                {
                    Console.WriteLine("Chat session is not initialized.");
                    result = string.Empty;
                }
                try
                {
                    File.Delete(tempImagePath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error deleting temporary files: {ex.Message}");
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending image: {ex.Message}");
                result = string.Empty;
            }
            return result;
        }


        private static async Task ProcessTtsQueue()
        {
            while (true)
            {
                if (_ttsQueue.Count > 0 && !_isTtsPlaying)
                {
                    string textToSynthesize = _ttsQueue.Dequeue();
                    await SynthesizeAndPlayAudio(textToSynthesize);
                }
                await Task.Delay(1);
            }
        }

        private static async Task SynthesizeAndPlayAudio(string text)
        {
            if (_tts == null)
            {
                Console.WriteLine("TTS not initialized.");
                return;
            }
            if (!_isRecording)
            {
                try
                {
                    var audio = _tts.Generate(text, 1.0f, 0);

                    if (audio != null && audio.Samples != null && audio.SampleRate > 0)
                    {
                        float[] samples = audio.Samples;
                        int sampleRate = audio.SampleRate;

                        byte[] byteBuffer = new byte[samples.Length * 4];

                        Buffer.BlockCopy(samples, 0, byteBuffer, 0, byteBuffer.Length);

                        WaveFormat waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

                        //Use 'using' statement to ensure the waveStream
                        using (var waveStream = new RawSourceWaveStream(new MemoryStream(byteBuffer), waveFormat))
                        {
                            // Create _outputDevice only if it's null
                            if (_outputDevice == null)
                            {
                                _outputDevice = new WaveOutEvent();
                                _outputDevice.PlaybackStopped += (sender, e) =>
                                {
                                    _isTtsPlaying = false;
                                    //_outputDevice?.Dispose(); // Don't dispose here, dispose in Main
                                    //_outputDevice = null;
                                };
                            }

                            _outputDevice.Init(waveStream);
                            _isTtsPlaying = true;
                            _outputDevice.Play();

                            while (_outputDevice != null && _outputDevice.PlaybackState == PlaybackState.Playing)
                            {
                                await Task.Delay(100);
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("TTS generated no audio data.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error synthesizing and playing audio: {ex.Message}");
                }
            }
        }

        private static void StopTtsPlayback()
        {
            if (_outputDevice != null && _outputDevice.PlaybackState == PlaybackState.Playing)
            {
                _outputDevice.Stop();
                _isTtsPlaying = false;
            }
        }

    }
}