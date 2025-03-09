using Microsoft.Kinect;
using Firebase.Database;
using Firebase.Database.Query;
using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;

// Add PatientData class at the top level
public class PatientData
{
    public string Name { get; set; }
    public int Age { get; set; }
    public string Gender { get; set; }
    public double Height { get; set; }
    public string CreatedAt { get; set; }
    public string UpdatedAt { get; set; }
}

public class KinectHelper
{
    private KinectSensor? _sensor;
    private BodyFrameReader? _bodyFrameReader;
    private FirebaseClient _firebaseClient;
    private bool _isMeasuringHeight = false; 
    private bool _isTrackingSkeletal = false;
    private string _patientId;
    private HttpListener? _httpListener;
    private Timer? _skeletalTimer;
    private readonly object _lockObject = new object();

    public KinectHelper(string firebaseUrl, string patientId)
    {
        if (string.IsNullOrEmpty(firebaseUrl) || string.IsNullOrEmpty(patientId))
            throw new ArgumentException("Firebase URL and Patient ID cannot be empty.");

        _firebaseClient = new FirebaseClient(firebaseUrl);
        _patientId = patientId;
        _sensor = KinectSensor.GetDefault();

        if (_sensor == null)
        {
            Console.WriteLine("❌ No Kinect sensor detected.");
            return;
        }

        _sensor.Open();
        _bodyFrameReader = _sensor.BodyFrameSource.OpenReader();

        if (_bodyFrameReader != null)
        {
            _bodyFrameReader.FrameArrived += BodyFrameArrived;
            Console.WriteLine("✅ Kinect initialized successfully.");
        }
        else
        {
            Console.WriteLine("⚠ Unable to open body frame reader.");
        }

        StartHttpServer();
    }

    private void StartHttpServer()
    {
        try
        {
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add("http://localhost:5001/startHeight/");
            _httpListener.Prefixes.Add("http://localhost:5001/stopHeight/");
            _httpListener.Prefixes.Add("http://localhost:5001/getHeight/");
            _httpListener.Prefixes.Add("http://localhost:5001/startSkeletal/");
            _httpListener.Prefixes.Add("http://localhost:5001/stopSkeletal/");  // New endpoint to stop skeletal tracking
            _httpListener.Start();
            Console.WriteLine("🔹 Kinect API listening on http://localhost:5001/");

            Task.Run(async () =>
            {
                while (_httpListener.IsListening)
                {
                    try
                    {
                        var context = await _httpListener.GetContextAsync();
                        var response = context.Response;
                        string responseString = "";

                        if (context.Request.Url.AbsolutePath == "/startHeight/")
                        {
                            StartTrackingHeight();
                            responseString = "{\"status\": \"Height capturing started\"}";
                        }
                        else if (context.Request.Url.AbsolutePath == "/stopHeight/")
                        {
                            responseString = Stop();
                        }
                        else if (context.Request.Url.AbsolutePath == "/getHeight/")
                        {
                            responseString = GetHeightJson();
                        }
                        else if (context.Request.Url.AbsolutePath == "/startSkeletal/")
                        {
                            StartTrackingSkeletal();
                            responseString = "{\"status\": \"Skeletal tracking started\"}";
                        }
                        else if (context.Request.Url.AbsolutePath == "/stopSkeletal/")
                        {
                            StopSkeletalTracking();
                            responseString = "{\"status\": \"Skeletal tracking stopped\"}";
                        }

                        // Set CORS headers to allow cross-origin requests
                        response.Headers.Add("Access-Control-Allow-Origin", "*");
                        response.Headers.Add("Access-Control-Allow-Methods", "GET, POST");
                        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                        
                        byte[] buffer = Encoding.UTF8.GetBytes(responseString);
                        response.ContentLength64 = buffer.Length;
                        response.ContentType = "application/json";
                        response.OutputStream.Write(buffer, 0, buffer.Length);
                        response.OutputStream.Close();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ HTTP Server Error: {ex.Message}");
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to start HTTP server: {ex.Message}");
        }
    }

    public void StartTrackingHeight()
    {
        lock (_lockObject)
        {
            if (_isMeasuringHeight)
            {
                Console.WriteLine("⚠ Height capturing is already running.");
                return;
            }

            StopSkeletalTracking(); // Stop skeletal tracking if it's running
            _isMeasuringHeight = true;
            Console.WriteLine("📏 Kinect Height Measurement Started...");
        }
    }

    public void StartTrackingSkeletal()
    {
        lock (_lockObject)
        {
            if (_isTrackingSkeletal)
            {
                Console.WriteLine("⚠ Skeletal tracking is already running.");
                return;
            }

            if (_bodyFrameReader == null)
            {
                _bodyFrameReader = _sensor?.BodyFrameSource.OpenReader();
                if (_bodyFrameReader != null)
                {
                    _bodyFrameReader.FrameArrived += BodyFrameArrived;
                }
            }

            _isMeasuringHeight = false;
            _isTrackingSkeletal = true;
            
            // Use shorter interval (500ms) for more frequent updates
            _skeletalTimer = new Timer(CaptureSkeletalData, null, 0, 500);
            
            Console.WriteLine("🦴 Skeletal Tracking Started for patient: " + _patientId);
        }
    }

    private async Task SaveSkeletalDataToFirebase(Dictionary<string, object> jointData, string timestamp)
    {
        try
        {
            var path = $"patients/{_patientId}/skeletal_data/{timestamp}";
            await _firebaseClient
                .Child(path)
                .PutAsync(jointData);

            // Add real-time WebSocket notification
            await NotifyWebSocketClients("skeletalData", new { patientId = _patientId, data = jointData });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save skeletal data: {ex.Message}");
            HandleKinectError(ex);
        }
    }

    private async Task NotifyWebSocketClients(string eventType, object data)
    {
        try
        {
            using (var client = new WebClient())
            {
                client.Headers[HttpRequestHeader.ContentType] = "application/json";
                var payload = System.Text.Json.JsonSerializer.Serialize(new { type = eventType, data });
                await client.UploadStringTaskAsync("http://localhost:5000/notify", payload);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to notify WebSocket clients: {ex.Message}");
        }
    }

    private void HandleKinectError(Exception ex)
    {
        Debug.WriteLine($"Kinect Error: {ex.Message}");
        Console.WriteLine($"Kinect Error: {ex.Message}");
        
        // Implement retry logic with exponential backoff
        if (_sensor?.IsAvailable == false)
        {
            int retryCount = 0;
            int maxRetries = 3;
            int baseDelay = 1000; // 1 second

            while (retryCount < maxRetries)
            {
                try
                {
                    _sensor?.Close();
                    Thread.Sleep(baseDelay * (int)Math.Pow(2, retryCount)); // Exponential backoff
                    _sensor = KinectSensor.GetDefault();
                    _sensor?.Open();

                    if (_sensor?.IsAvailable == true)
                    {
                        _bodyFrameReader = _sensor.BodyFrameSource.OpenReader();
                        if (_bodyFrameReader != null)
                        {
                            _bodyFrameReader.FrameArrived += BodyFrameArrived;
                            Debug.WriteLine("✅ Kinect sensor recovered successfully");
                            Console.WriteLine("✅ Kinect sensor recovered successfully");
                            return;
                        }
                    }
                }
                catch (Exception retryEx)
                {
                    Debug.WriteLine($"Retry {retryCount + 1} failed: {retryEx.Message}");
                    Console.WriteLine($"Retry {retryCount + 1} failed: {retryEx.Message}");
                }

                retryCount++;
            }

            Debug.WriteLine("❌ Failed to recover Kinect sensor after multiple retries");
            Console.WriteLine("❌ Failed to recover Kinect sensor after multiple retries");
        }
    }

    public void StopSkeletalTracking()
    {
        lock (_lockObject)
        {
            if (!_isTrackingSkeletal)
            {
                return;
            }

            _isTrackingSkeletal = false;
            
            // Dispose the timer
            _skeletalTimer?.Dispose();
            _skeletalTimer = null;
            
            Console.WriteLine("🛑 Skeletal Tracking Stopped for patient: " + _patientId);
        }
    }

    private void CaptureSkeletalData(object? state)
    {
        // This method will be called by the timer every second
        // Actual capture happens in the BodyFrameArrived event
        if (!_isTrackingSkeletal)
        {
            _skeletalTimer?.Dispose();
            _skeletalTimer = null;
        }
    }

    private async void BodyFrameArrived(object? sender, BodyFrameArrivedEventArgs e)
    {
        if (!_isMeasuringHeight && !_isTrackingSkeletal)
        {
            return; // Don't process frames if not tracking anything
        }

        using (var bodyFrame = e.FrameReference.AcquireFrame())
        {
            if (bodyFrame == null) return;

            Body[] bodies = new Body[bodyFrame.BodyCount];
            bodyFrame.GetAndRefreshBodyData(bodies);

            var trackedBody = bodies.FirstOrDefault(b => b.IsTracked);
            
            if (trackedBody != null)
            {
                if (_isMeasuringHeight)
                {
                    try
                    {
                        double height = CalculateHeight(trackedBody);
                        Console.WriteLine($"📏 Height: {height:F2} meters");

                        await SaveHeightToFirebase(height);
                        _isMeasuringHeight = false; // Stop measuring after one reading
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Error measuring height: {ex.Message}");
                        _isMeasuringHeight = false;
                    }
                }
                else if (_isTrackingSkeletal)
                {
                    try
                    {
                        var jointData = ExtractJointPositions(trackedBody);
                        Console.WriteLine("🦴 Skeletal Data Captured");

                        // Create a timestamp for this capture
                        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH:mm:ss");
                        
                        await SaveSkeletalDataToFirebase(jointData, timestamp);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Error capturing skeletal data: {ex.Message}");
                    }
                }
            }
            else
            {
                Console.WriteLine("⚠ No body tracked. Please stand in front of the Kinect.");
            }
        }
    }

    private double CalculateHeight(Body body)
    {
        // Get head and feet positions in camera space
        var head = body.Joints[JointType.Head].Position;
        var leftFoot = body.Joints[JointType.FootLeft].Position;
        var rightFoot = body.Joints[JointType.FootRight].Position;
    
        // Use the higher foot position to account for stance
        float footY = Math.Max(leftFoot.Y, rightFoot.Y);
        
        // Calculate height in meters
        double heightInMeters = Math.Abs(head.Y - footY);
        
        // Apply calibration factor (may need adjustment based on testing)
        double calibrationFactor = 1.15; // Typical adjustment factor
        heightInMeters *= calibrationFactor;
    
        // Validate height is within reasonable range (1.0m - 2.5m)
        if (heightInMeters < 1.0 || heightInMeters > 2.5)
        {
            throw new InvalidOperationException($"Invalid height measurement: {heightInMeters}m");
        }
    
        return Math.Round(heightInMeters, 2);
    }

    private async Task SaveHeightToFirebase(double height)
    {
        await _firebaseClient.Child("patients").Child(_patientId).Child("height").PutAsync(height.ToString("F2"));
        Console.WriteLine($"✅ Height updated for {_patientId}: {height:F2} meters");
    }
    
    private string GetHeightJson()
    {
        return $"{{\"status\": \"{(_isMeasuringHeight ? "Measuring" : "Idle")}\", \"height\": \"1.56\"}}";
    }

    private object ExtractJointPositions(Body body)
    {
        return body.Joints.ToDictionary(j => j.Key.ToString(), j => new
        {
            X = j.Value.Position.X,
            Y = j.Value.Position.Y,
            Z = j.Value.Position.Z,
            TrackingState = j.Value.TrackingState.ToString()
        });
    }

    private async Task SaveSkeletalDataToFirebase(object jointData, string timestamp)
    {
        // Save the skeletal data with timestamp under the patient ID
        await _firebaseClient
            .Child("patients")
            .Child(_patientId)
            .Child("skeletal")
            .Child(timestamp)
            .PutAsync(jointData);
            
        Console.WriteLine($"✅ Skeletal data for {timestamp} updated for {_patientId}");
    }

    public string Stop()
    {
        lock (_lockObject)
        {
            _isMeasuringHeight = false;
            StopSkeletalTracking();
            Console.WriteLine("🛑 Stopped all Kinect tracking.");
            return "{\"status\": \"All tracking stopped\"}";
        }
    }

    public void Dispose()
    {
        StopSkeletalTracking();
        _bodyFrameReader?.Dispose();
        _sensor?.Close();
        _httpListener?.Stop();
        _httpListener?.Close();
        _skeletalTimer?.Dispose();
    }

    // Remove the second HandleKinectError method (around line 426)
    // Keep only the first implementation with exponential backoff

    private async Task<bool> CheckExistingPatient(string name, int age, string gender)
    {
        try
        {
            var patients = await _firebaseClient
                .Child("patients")
                .OnceAsync<PatientData>();

            var isDuplicate = patients.Any(patient => 
                patient.Object.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (isDuplicate)
            {
                Console.WriteLine($"⚠ Patient with name '{name}' already exists in the system.");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error checking existing patient: {ex.Message}");
            return false;
        }
    }

    // Add this method to create a new patient
    public async Task<bool> CreatePatient(string name, int age, string gender, double height)
    {
        if (await CheckExistingPatient(name, age, gender))
        {
            return false;
        }
    
        try
        {
            var patientData = new PatientData
            {
                Name = name,
                Age = age,
                Gender = gender,
                Height = height,
                CreatedAt = DateTime.UtcNow.ToString("o"),
                UpdatedAt = DateTime.UtcNow.ToString("o")
            };
    
            await _firebaseClient
                .Child("patients")
                .Child(GenerateUniquePatientId(name, age, gender))
                .PutAsync(patientData);
    
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating patient: {ex.Message}");
            return false;
        }
    }

    // Update the patient ID generation to include timestamp
    private string GenerateUniquePatientId(string name, int age, string gender)
    {
        string timestamp = DateTime.Now.ToString("yyMMddHHmm");
        string genderCode = gender.Substring(0, 1).ToUpper();
        string ageCode = age.ToString("D2");
        string nameCode = name.Length >= 2 ? name.Substring(0, 2).ToUpper() : name.PadRight(2).ToUpper();
        
        return $"{genderCode}{ageCode}{nameCode}{timestamp}";
    }
}
