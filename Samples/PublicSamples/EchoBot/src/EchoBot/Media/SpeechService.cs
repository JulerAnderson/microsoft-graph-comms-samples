using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Skype.Bots.Media;
using System.Runtime.InteropServices;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EchoBot.Media
{
    /// <summary>
    /// Class SpeechService.
    /// </summary>
    public class SpeechService
    {
        /// <summary>
        /// The is the indicator if the media stream is running
        /// </summary>
        private bool _isRunning = false;
        /// <summary>
        /// The is draining indicator
        /// </summary>
        protected bool _isDraining;

        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger _logger;
        private readonly PushAudioInputStream _audioInputStream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
        private readonly AudioOutputStream _audioOutputStream = AudioOutputStream.CreatePullStream();

        private readonly SpeechConfig _speechConfig;
        private SpeechRecognizer _recognizer;
        private readonly SpeechSynthesizer _synthesizer;

        // Watson Assistant configuration
        private const string WatsonApiKey = "seNs0dw9bNzaHmHWtyn4DJ-1jnbnD9_2i6lvwqqhuPPT";
        private const string WatsonAssistantId = "243daed3-b762-4ecb-acbd-ccbb8da4aecb";
        private const string WatsonInstanceUrl = "https://api.eu-gb.assistant-builder.watson.cloud.ibm.com";
        private const string WatsonApiVersion = "2024-08-25";

        private string _watsonSessionId;
        private readonly HttpClient _httpClient;

        // Agregar un indicador de estado
        private bool _isProcessingResponse = false;

        /// <summary>
        /// Initializes a new instance of the <see cref="SpeechService" /> class.
        public SpeechService(AppSettings settings, ILogger logger)
        {
            _logger = logger;

            _speechConfig = SpeechConfig.FromSubscription(settings.SpeechConfigKey, settings.SpeechConfigRegion);
            _speechConfig.SpeechSynthesisLanguage = settings.BotLanguage;
            _speechConfig.SpeechRecognitionLanguage = settings.BotLanguage;

            var audioConfig = AudioConfig.FromStreamOutput(_audioOutputStream);
            _synthesizer = new SpeechSynthesizer(_speechConfig, audioConfig);

            _httpClient = new HttpClient();
        }

        /// <summary>
        /// Appends the audio buffer.
        /// </summary>
        /// <param name="audioBuffer"></param>
        public async Task AppendAudioBuffer(AudioMediaBuffer audioBuffer)
        {
            _logger.LogInformation("AppendAudioBuffer called");
            if (!_isRunning)
            {
                Start();
                await ProcessSpeech();
            }

            try
            {
                // audio for a 1:1 call
                var bufferLength = audioBuffer.Length;
                if (bufferLength > 0)
                {
                    var buffer = new byte[bufferLength];
                    Marshal.Copy(audioBuffer.Data, buffer, 0, (int)bufferLength);

                    _audioInputStream.Write(buffer);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Exception happend writing to input stream");
            }
        }

        public virtual void OnSendMediaBufferEventArgs(object sender, MediaStreamEventArgs e)
        {
            if (SendMediaBuffer != null)
            {
                SendMediaBuffer(this, e);
            }
        }

        public event EventHandler<MediaStreamEventArgs> SendMediaBuffer;

        /// <summary>
        /// Ends this instance.
        /// </summary>
        /// <returns>Task.</returns>
        public async Task ShutDownAsync()
        {
            if (!_isRunning)
            {
                return;
            }

            if (_isRunning)
            {
                await _recognizer.StopContinuousRecognitionAsync();
                _recognizer.Dispose();
                _audioInputStream.Close();

                _audioInputStream.Dispose();
                _audioOutputStream.Dispose();
                _synthesizer.Dispose();

                _isRunning = false;
            }
        }

        /// <summary>
        /// Starts this instance.
        /// </summary>
        private void Start()
        {
            if (!_isRunning)
            {
                _isRunning = true;
            }
        }

        /// <summary>
        /// Processes this instance.
        /// </summary>
        private async Task ProcessSpeech()
        {
            try
            {
                var stopRecognition = new TaskCompletionSource<int>();

                using (var audioInput = AudioConfig.FromStreamInput(_audioInputStream))
                {
                    if (_recognizer == null)
                    {
                        _logger.LogInformation("init recognizer");
                        _recognizer = new SpeechRecognizer(_speechConfig, audioInput);
                    }
                }

                _recognizer.Recognizing += (s, e) =>
                {
                    if (_isProcessingResponse)
                    {
                        _logger.LogInformation("Ignoring input because a response is being processed.");
                        return;
                    }
                    _logger.LogInformation($"RECOGNIZING: Text={e.Result.Text}");
                };

                _recognizer.Recognized += async (s, e) =>
                {
                    if (_isProcessingResponse)
                    {
                        _logger.LogInformation("Ignoring recognized input because a response is being processed.");
                        return;
                    }
                    if (e.Result.Reason == ResultReason.RecognizedSpeech)
                    {
                        if (string.IsNullOrEmpty(e.Result.Text))
                            return;

                        _logger.LogInformation($"RECOGNIZED: Text={e.Result.Text}");
                        // We recognized the speech
                        // Now do Speech to Text
                        await TextToSpeech(e.Result.Text);
                    }
                    else if (e.Result.Reason == ResultReason.NoMatch)
                    {
                        _logger.LogInformation($"NOMATCH: Speech could not be recognized.");
                    }
                };

                _recognizer.Canceled += (s, e) =>
                {
                    _logger.LogInformation($"CANCELED: Reason={e.Reason}");

                    if (e.Reason == CancellationReason.Error)
                    {
                        _logger.LogInformation($"CANCELED: ErrorCode={e.ErrorCode}");
                        _logger.LogInformation($"CANCELED: ErrorDetails={e.ErrorDetails}");
                        _logger.LogInformation($"CANCELED: Did you update the subscription info?");
                    }

                    stopRecognition.TrySetResult(0);
                };

                _recognizer.SessionStarted += async (s, e) =>
                {
                    // string saludo = await SendMessageToWatsonAsync("Hola");
                    // await TextToSpeech(saludo);
                    _logger.LogInformation("\nSession started event.");
                    _logger.LogInformation($"[WATSONXAI] Creo que aquí es cuando invocas al bot");
                    // await SpeakRawTextAsync("Buenas tardes, soy TGI, ¿en qué puedo ayudarle hoy?");
                };

                _recognizer.SessionStopped += (s, e) =>
                {
                    _logger.LogInformation("\nSession stopped event.");
                    _logger.LogInformation("\nStop recognition.");
                    _logger.LogInformation($"[WATSONXAI] Creo que aquí es cuando botas al bot");
                    stopRecognition.TrySetResult(0);
                };

                // Starts continuous recognition. Uses StopContinuousRecognitionAsync() to stop recognition.
                await _recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);

                // Waits for completion.
                // Use Task.WaitAny to keep the task rooted.
                Task.WaitAny(new[] { stopRecognition.Task });

                // Stops recognition.
                await _recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogError(ex, "The queue processing task object has been disposed.");
            }
            catch (Exception ex)
            {
                // Catch all other exceptions and log
                _logger.LogError(ex, "Caught Exception");
            }

            _isDraining = false;
        }

        // private async Task SpeakRawTextAsync(string text)
        // {
        //     try
        //     {
        //         _logger.LogInformation("Speaking raw text directly: {Text}", text);

        //         SpeechSynthesisResult result = await _synthesizer.SpeakTextAsync(text);

        //         using (var stream = AudioDataStream.FromResult(result))
        //         {
        //             var currentTick = DateTime.Now.Ticks;
        //             MediaStreamEventArgs args = new MediaStreamEventArgs
        //             {
        //                 AudioMediaBuffers = Util.Utilities.CreateAudioMediaBuffers(stream, currentTick, _logger)
        //             };
        //             OnSendMediaBufferEventArgs(this, args);
        //         }
        //     }
        //     catch (Exception ex)
        //     {
        //         _logger.LogError(ex, "Error in SpeakRawTextAsync");
        //     }
        // }


        private async Task<string> CreateWatsonSessionAsync()
        {
            try
            {
                var url = $"{WatsonInstanceUrl}/v2/assistants/{WatsonAssistantId}/sessions?version={WatsonApiVersion}";
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("Authorization", $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"apikey:{WatsonApiKey}"))}");

                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var responseContent = await response.Content.ReadAsStringAsync();
                var jsonResponse = JsonSerializer.Deserialize<WatsonSessionResponse>(responseContent);

                _logger.LogInformation($"[WATSONXAI] Nueva sesión creada: {jsonResponse.SessionId}");
                return jsonResponse.SessionId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create Watson session.");
                throw;
            }
        }

        public async Task<string> SendMessageToWatsonAsync(string message)
        {
            try
            {
                if (string.IsNullOrEmpty(_watsonSessionId))
                {
                    _watsonSessionId = await CreateWatsonSessionAsync();
                }

                if (string.IsNullOrEmpty(_watsonSessionId))
                {
                    throw new InvalidOperationException("Watson session ID is null or empty.");
                }

                var url = $"{WatsonInstanceUrl}/v2/assistants/{WatsonAssistantId}/sessions/{_watsonSessionId}/message?version={WatsonApiVersion}";
                var requestContent = new
                {
                    input = new { text = message }
                };

                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent), Encoding.UTF8, "application/json")
                };
                request.Headers.Add("Authorization", $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"apikey:{WatsonApiKey}"))}");
                _logger.LogInformation($"[WATSONXAI] Se envía a Watson: {requestContent}");
                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var responseContent = await response.Content.ReadAsStringAsync();
                _logger?.LogInformation("Watson Assistant raw response: {ResponseContent}", responseContent);
                _logger.LogInformation($"[WATSONXAI] Watson responde: {responseContent}");

                // Deserializar la respuesta como un objeto dinámico para inspeccionar la estructura
                var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);

                // Inspeccionar la estructura de la respuesta
                if (jsonResponse.TryGetProperty("output", out var output) &&
                    output.TryGetProperty("generic", out var generic) &&
                    generic.ValueKind == JsonValueKind.Array)
                {
                    var concatenatedResponse = new StringBuilder();

                    foreach (var item in generic.EnumerateArray())
                    {
                        if (item.TryGetProperty("response_type", out var responseType) &&
                            responseType.GetString() == "text" &&
                            item.TryGetProperty("text", out var text))
                        {
                            // Agregar el texto con un punto y un espacio al final
                            concatenatedResponse.Append(text.GetString());
                            concatenatedResponse.Append(". ");
                        }
                    }

                    var finalResponse = concatenatedResponse.ToString().Trim().Replace("<close></close>","");
                    if (!string.IsNullOrEmpty(finalResponse))
                    {
                        _logger.LogInformation($"[WATSONXAI] El Speech debería decir: {finalResponse}"); 
                        return finalResponse;
                    }
                }

                _logger?.LogWarning("Watson Assistant response does not contain valid 'Generic' data.");
                throw new InvalidOperationException("Watson Assistant response is null or invalid.");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to send message to Watson Assistant.");
                throw;
            }
        }

        private async Task TextToSpeech(string text)
        {
            try
            {
                _logger.LogInformation("Processing text with Watson Assistant...");

                // Pausar la recepción y transcripción de audio
                if (_recognizer != null && !_isProcessingResponse)
                {
                    _isProcessingResponse = true;
                    //await _recognizer.StopContinuousRecognitionAsync();
                    _logger.LogInformation("[WATSONXAI] Recognition paused while processing response.");
                }

                // Eliminar el punto final si existe
                if (text.EndsWith("."))
                {
                    text = text.TrimEnd('.');
                    _logger.LogInformation($"SE ENVÍA A WATSON SIN PUNTO: {text}");
                }

                var watsonResponse = await SendMessageToWatsonAsync(text);

                if (!string.IsNullOrEmpty(watsonResponse))
                {
                    _logger.LogInformation("[WATSONXAI] CONVERTIENDO TEXTO A VOZ");
                    SpeechSynthesisResult result = await _synthesizer.SpeakTextAsync(watsonResponse);

                    using (var stream = AudioDataStream.FromResult(result))
                    {
                        var currentTick = DateTime.Now.Ticks;
                        MediaStreamEventArgs args = new MediaStreamEventArgs
                        {
                            AudioMediaBuffers = Util.Utilities.CreateAudioMediaBuffers(stream, currentTick, _logger)
                        };
                        OnSendMediaBufferEventArgs(this, args);
                    }
                }
                else
                {
                    _logger.LogWarning("Watson Assistant returned an empty response.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during TextToSpeech processing.");
            }
            finally
            {
                // Reanudar la recepción y transcripción de audio
                if (_recognizer != null && _isProcessingResponse)
                {
                    //await _recognizer.StartContinuousRecognitionAsync();
                    _isProcessingResponse = false;
                    _logger.LogInformation("[WATSONXAI] Recognition resumed after processing response.");
                }
            }
        }

        private class WatsonSessionResponse
        {
            [JsonPropertyName("session_id")]
            public string SessionId { get; set; }
        }

        private class WatsonMessageResponse
        {
            public WatsonOutput Output { get; set; }
        }

        private class WatsonOutput
        {
            public List<WatsonGenericResponse> Generic { get; set; }
        }

        private class WatsonGenericResponse
        {
            [JsonPropertyName("response_type")]
            public string ResponseType { get; set; }

            [JsonPropertyName("text")]
            public string Text { get; set; }
        }
    }
}
