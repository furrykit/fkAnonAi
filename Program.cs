using GenerativeAI.Methods;
using GenerativeAI.Models;
using GenerativeAI.Types;
using NAudio.Wave;
using SherpaOnnx;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace fkAnonAi
{
    internal class Program
    {
        private static List<byte> _audioBuffer = new List<byte>();
        private static int _maxRetries = 1;
        private static string? _apiKey = "default_key"; // Default API Key
        private static double _amplitudeThreshold = 0.02;
        private static int _selectedDeviceNumber = 0;
        private static Queue<byte[]> _audioQueue = new Queue<byte[]>();
        private static bool _isProcessing = false;
        private static bool _isRecording = false;
        private static DateTime _lastAmplitudeAboveThresholdTime = DateTime.MinValue;
        private const int _silenceDelayMs = 3000; // Задержка до деактивации микрофона

        private static OfflineTts? _tts;
        private static WaveOutEvent? _outputDevice;
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
                            _apiKey = args[i + 1];
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
                            var proxyUsername = string.Empty;
                            var proxyPassword = string.Empty;

                            if (proxyAddressString.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
                            {
                                var proxySettings = proxyAddressString.Substring("socks5://".Length).Split(':'); // Удаляем socks5:// для разбора параметров
                                if (proxySettings.Length == 2)
                                {
                                    _proxyAddress = proxyAddressString; // Сохраняем исходную строку, включая socks5://
                                    _proxyUsername = null;
                                    _proxyPassword = null;
                                }
                                else if (proxySettings.Length >= 4)
                                {
                                    _proxyAddress = proxyAddressString; // Сохраняем исходную строку, включая socks5://
                                    _proxyUsername = proxySettings[2];
                                    _proxyPassword = string.Join(":", proxySettings.Skip(3));
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
                                    _proxyUsername = null;
                                    _proxyPassword = null;
                                }
                                else if (proxySettings.Length >= 4)
                                {
                                    _proxyAddress = proxyAddressString;
                                    _proxyUsername = proxySettings[2];
                                    _proxyPassword = string.Join(":", proxySettings.Skip(3));
                                }
                                else
                                {
                                    Console.WriteLine("Invalid proxy format. Use -proxy address:port or -proxy address:port:username:password or -proxy socks5://address:port or -proxy socks5://address:port:username:password");
                                    return;
                                }
                            }
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
            //Console.WriteLine($"Using API Key: {_apiKey}");
            //Console.WriteLine($"Using Proxy: {_proxyAddress}, User: {_proxyUsername}");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("fkAnonAi v1.0.2 https://t.me/furrykit");
            Console.ResetColor();
            // Настройки прокси
            HttpClient httpClient = null;
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


            Console.WriteLine("Подключаемся к серверам Google...");

            try
            {
                _model = new GenerativeModel(_apiKey, "gemini-1.5-flash", client: httpClient); //latest gemini-2.0-flash-exp

                if (_model == null)
                {
                    Console.WriteLine("Failed to initialize Generative Model");
                    return;
                }
                _chat = _model.StartChat(new StartChatParams());
                if (_chat == null)
                {
                    Console.WriteLine("Failed to start chat session.");
                    return;
                }
                await _chat.SendMessageAsync(_gLaDOSPrompt);
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
            _screenAnalysisTimer.Elapsed += async (sender, e) => await EnqueueScreenAnalysis();
            _screenAnalysisTimer.Start();

            //Console.WriteLine($"Screen analysis will run every {_screenshotInterval / 1000} seconds.");


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
                        // Console.WriteLine("Caps Lock активирован. Деактивация микрофона...");
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

            // Добавим Timer для периодической проверки Caps Lock и деактивации микрофона
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
                    // Console.WriteLine("Caps Lock активирован. Деактивация микрофона...");
                    _isRecording = false;
                    _audioBuffer.Clear();
                }
            };
            capsLockTimer.Start();

            // Set initial Caps Lock state
            _lastCapsLockState = IsCapsLockActive();

            waveIn.StartRecording();

            _ = Task.Run(async () => await ProcessAudioQueue(waveIn));
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
                        if (settings.Length == 1)
                        {
                            _proxyAddress = settings[0];
                            _proxyUsername = null;
                            _proxyPassword = null;
                        }
                        else if (settings.Length == 3)
                        {
                            _proxyAddress = settings[0];
                            _proxyUsername = settings[1];
                            _proxyPassword = settings[2];
                        }
                        else
                        {
                            Console.WriteLine("Invalid proxy settings.");
                        }
                        Console.WriteLine($"Proxy settigns updated to: {_proxyAddress} user: {_proxyUsername}");


                    }
                    else
                    {
                        Console.WriteLine("Invalid interval value.");
                    }
                }
            }

            Console.WriteLine("Chat ended.");
            _tts?.Dispose();
            _outputDevice?.Dispose();
        }

        private static async Task EnqueueScreenAnalysis()
        {
            await Task.Run(() => _requestQueue.Enqueue(async () => await AnalyzeScreen()));
        }

        private static byte[] CaptureScreen(bool saveToFile = false)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Console.WriteLine("Screen capture is only supported on Windows.");
                return Array.Empty<byte>();
            }

            int screenWidth = 3840; // Разрешение для захвата двух мониторов с разрешениями 1920x1080
            int screenHeight = 1080;
            var bmp = new Bitmap(screenWidth, screenHeight);
            using (var grfx = Graphics.FromImage(bmp))
            {
                grfx.CopyFromScreen(0, 0, 0, 0, bmp.Size);
            }

            byte[] imageBytes;
            using (var memoryStream = new MemoryStream())
            {
                //сохранить в пнг
                //if (saveToFile)
                //{
                //    string fileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png"; // Формат PNG
                //    string filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);

                //    bmp.Save(filePath, ImageFormat.Png); // Сохраняем как PNG
                //    Console.WriteLine($"Скриншот сохранен в: {filePath}");
                //}

                // или жпег
                var jpegEncoder = GetEncoder(ImageFormat.Jpeg);
                var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 80L);

                if (saveToFile)
                {
                    string fileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.jpg"; // Формат JPG
                    string filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);


                    bmp.Save(filePath, jpegEncoder, encoderParams); // Сохраняем как жпег на диск
                    Console.WriteLine($"Скриншот сохранен в: {filePath}");
                }

                // передать апи в пнг
                // bmp.Save(memoryStream, ImageFormat.Png);
                // передать апи в жпег
                bmp.Save(memoryStream, jpegEncoder, encoderParams); // Сохраняем в memoryStream как жпег

                imageBytes = memoryStream.ToArray();
            }

            return imageBytes;
        }

        private static ImageCodecInfo GetEncoder(ImageFormat format)
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

        private static async Task AnalyzeScreen()
        {
            try
            {
                // Console.WriteLine("Analyzing screen...");
                byte[] imageBytes = CaptureScreen(); // (true) чтобы включить сохранение скриншотов для отладки
                if (imageBytes.Length == 0)
                {
                    return; // если захват не удался, выходим
                }

                var parts = new List<Part>
        {
            new Part
            {
                InlineData = new GenerativeContentBlob() {
                    Data = Convert.ToBase64String(imageBytes),
                    //MimeType = "image/png" // для скриншотов в пнг
                    MimeType = "image/jpeg" // для скриншотов в жпеге
                }
            },

        };

                if (_chat != null)
                {
                    using (var cts = new CancellationTokenSource(_responseTimeout))
                    {
                        var resultTask = _chat.SendMessageAsync(parts.ToArray(), cts.Token);

                        var completedTask = await Task.WhenAny(resultTask, Task.Delay(_responseTimeout, cts.Token));

                        if (completedTask == resultTask)
                        {
                            var result = await resultTask;
                            if (result?.Candidates != null && result.Candidates.Any())
                            {
                                string? visionDescription = result.Candidates.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
                                if (!string.IsNullOrEmpty(visionDescription))
                                {
                                    visionDescription = visionDescription.Replace("*", "");
                                    Console.WriteLine($"GLaDOS (подсматривает): {visionDescription}");
                                    _ttsQueue.Enqueue(visionDescription);
                                }
                                else
                                {
                                    Console.WriteLine("GLaDOS (подсматривает): No text in response.");
                                }
                            }
                            else
                            {
                                Console.WriteLine("GLaDOS (подсматривает): No candidates in response.");
                            }
                        }
                        else
                        {
                            Console.WriteLine("GLaDOS (подсматривает): Не ответила вовремя (таймаут).");
                            cts.Cancel();
                        }
                    }
                }
                else
                {
                    Console.WriteLine("Chat session is not initialized.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error analyzing screen: {ex.Message}");
            }
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

        private static async Task ProcessAudioQueue(WaveInEvent waveIn)
        {
            while (true)
            {
                if (_audioQueue.Count > 0 && !_isProcessing)
                {
                    _isProcessing = true;
                    byte[] bufferToProcess = _audioQueue.Dequeue();
                    try
                    {
                        await ProcessAudioBuffer(waveIn, bufferToProcess, waveIn.WaveFormat); // Передаем waveFormat
                    }
                    finally
                    {
                        _isProcessing = false;
                    }
                }
                await Task.Delay(1);
            }
        }
        private static async Task ProcessAudioBuffer(WaveInEvent waveIn, byte[] buffer, WaveFormat waveFormat)
        {
            try
            {
                // Сохраняем аудио в файл для отладки
                //string debugAudioPath = Path.Combine(Directory.GetCurrentDirectory(), $"debug_audio_{DateTime.Now:yyyyMMdd_HHmmss_fff}.wav");
                //try
                //{
                //    using (WaveFileWriter waveFileWriter = new WaveFileWriter(debugAudioPath, waveFormat))
                //    {
                //        waveFileWriter.Write(buffer, 0, buffer.Length);
                //    }
                //    Console.WriteLine($"Сохранено аудио для отладки: {debugAudioPath}");
                //}
                //catch (Exception ex)
                //{
                //    Console.WriteLine($"Ошибка при сохранении отладочного аудио: {ex.Message}");
                //}

                // Захватываем скриншот
                byte[] imageBytes = CaptureScreen();

                // Отправляем WAV файл и скриншот напрямую в Gemini API
                string geminiResponse = await SendAudioAndImageWithRetries(buffer, waveFormat, imageBytes);
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

        private static async Task<string> SendAudioAndImageWithRetries(byte[] audioData, WaveFormat waveFormat, byte[] imageBytes)
        {
            int retries = 0;
            while (retries < _maxRetries)
            {
                try
                {
                    // Конвертируем byte[] в Stream, чтобы отправить в Part
                    using (MemoryStream audioStream = new MemoryStream(audioData))
                    {
                        // Сохраняем аудио в формате WAV в MemoryStream
                        using (MemoryStream wavStream = new MemoryStream())
                        {
                            using (WaveFileWriter waveFileWriter = new WaveFileWriter(wavStream, waveFormat))
                            {
                                waveFileWriter.Write(audioData, 0, audioData.Length);
                            }
                            // Получаем byte[] из MemoryStream с WAV
                            byte[] wavBytes = wavStream.ToArray();

                            var parts = new List<Part>
                            {
                                new Part
                                {
                                    InlineData = new GenerativeContentBlob()
                                    {
                                        Data = Convert.ToBase64String(wavBytes), // Отправляем WAV как base64
                                        MimeType = "audio/wav" // Указываем MIME type
                                    }
                                },
                                 new Part
                                {
                                  InlineData = new GenerativeContentBlob() {
                                    Data = Convert.ToBase64String(imageBytes),
                                    //MimeType = "image/png" // для скриншотов в пнг
                                    MimeType = "image/jpeg"  // для скриншотов в жпеге
                                }
                                }

                            };


                            if (_chat != null)
                            {
                                using (var cts = new CancellationTokenSource(_responseTimeout))
                                {
                                    var resultTask = _chat.SendMessageAsync(parts.ToArray(), cts.Token);

                                    var completedTask = await Task.WhenAny(resultTask, Task.Delay(_responseTimeout, cts.Token));

                                    if (completedTask == resultTask)
                                    {
                                        var result = await resultTask;
                                        if (result?.Candidates != null && result.Candidates.Any())
                                        {
                                            string? geminiResponse = result.Candidates.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
                                            if (!string.IsNullOrEmpty(geminiResponse))
                                            {
                                                geminiResponse = geminiResponse.Replace("*", "");
                                                Console.WriteLine($"GLaDOS: {geminiResponse}\n");
                                                return geminiResponse;
                                            }
                                        }
                                        Console.WriteLine("Gemini API не вернул текст.");
                                        return string.Empty;
                                    }
                                    else
                                    {
                                        Console.WriteLine("GLaDOS не ответила вовремя (таймаут).");
                                        cts.Cancel();
                                        return string.Empty;
                                    }
                                }
                            }
                            Console.WriteLine("Chat session is not initialized.");
                            return string.Empty;

                        }

                    }
                }
                catch (GenerativeAI.Exceptions.GenerativeAIException ex) when (ex.Message.Contains("RESOURCE_EXHAUSTED"))
                {
                    retries++;
                    int delay = (int)Math.Pow(2, retries);
                    Console.WriteLine($"Error: {ex.Message}, retrying in {delay} seconds");
                    await Task.Delay(delay * 1000);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error sending audio: {ex.Message}");
                    return string.Empty;
                }
            }
            Console.WriteLine($"Failed to send message after {_maxRetries} retries.");
            return string.Empty;
        }
        private static async Task<string> SendWithRetries(string text, byte[] imageBytes)
        {
            int retries = 0;
            while (retries < _maxRetries)
            {
                try
                {
                    var parts = new List<Part>
                    {
                         new Part
                        {
                          InlineData = new GenerativeContentBlob() {
                            Data = Convert.ToBase64String(imageBytes),
                            //MimeType = "image/png" // для скриншотов в пнг
                            MimeType = "image/jpeg"  // для скриншотов в жпеге
                        }
                        },
                        new Part { Text = text }
                    };
                    if (_chat != null)
                    {
                        using (var cts = new CancellationTokenSource(_responseTimeout))
                        {
                            var resultTask = _chat.SendMessageAsync(parts.ToArray(), cts.Token);

                            var completedTask = await Task.WhenAny(resultTask, Task.Delay(_responseTimeout, cts.Token));

                            if (completedTask == resultTask)
                            {
                                var result = await resultTask;
                                if (result?.Candidates != null && result.Candidates.Any())
                                {
                                    string? geminiResponse = result.Candidates.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
                                    if (!string.IsNullOrEmpty(geminiResponse))
                                    {
                                        geminiResponse = geminiResponse.Replace("*", "");
                                        Console.WriteLine($"GLaDOS: {geminiResponse}\n");
                                        return geminiResponse;
                                    }
                                }
                                return string.Empty;
                            }
                            else
                            {
                                Console.WriteLine("GLaDOS не ответила вовремя (таймаут).");
                                cts.Cancel();
                                return string.Empty;
                            }
                        }
                    }
                    return string.Empty;

                }
                catch (GenerativeAI.Exceptions.GenerativeAIException ex) when (ex.Message.Contains("RESOURCE_EXHAUSTED"))
                {
                    retries++;
                    int delay = (int)Math.Pow(2, retries);
                    Console.WriteLine($"Error: {ex.Message}, retrying in {delay} seconds");
                    await Task.Delay(delay * 1000);
                }
            }
            Console.WriteLine($"Failed to send message after {_maxRetries} retries.");
            return string.Empty;
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
                    // Console.WriteLine($"Synthesizing: {text}");
                    var audio = _tts.Generate(text, 1.0f, 0);

                    string outputFilename = Path.Combine(Directory.GetCurrentDirectory(), $"output_{Guid.NewGuid()}.wav");
                    bool success = audio.SaveToWaveFile(outputFilename);

                    if (success)
                    {
                        // Console.WriteLine($"Successfully saved audio to {outputFilename}");
                        using (var audioFile = new AudioFileReader(outputFilename))
                        {
                            _outputDevice = new WaveOutEvent();
                            _outputDevice.Init(audioFile);
                            _outputDevice.PlaybackStopped += async (sender, e) =>
                            {
                                _isTtsPlaying = false;
                                _outputDevice?.Dispose();
                                _outputDevice = null;

                                await Task.Delay(100); // Даем время на освобождение файла
                                try
                                {
                                    if (File.Exists(outputFilename))
                                    {
                                        File.Delete(outputFilename);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Error deleting file: {ex.Message}");
                                }
                            };
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
                        Console.WriteLine($"Failed to save audio to {outputFilename}");
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
                // Console.WriteLine("Stopping TTS playback.");
                _outputDevice.Stop();
                _isTtsPlaying = false;
            }
        }

    }
}