using Microsoft.Kinect;
using Firebase.Database;
using Firebase.Database.Query;
using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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

            _isMeasuringHeight = false; // Stop height tracking if it's running
            _isTrackingSkeletal = true;
            
            // Initialize the timer to capture skeletal data every second
            _skeletalTimer = new Timer(CaptureSkeletalData, null, 0, 1000);
            
            Console.WriteLine("🦴 Skeletal Tracking Started for patient: " + _patientId);
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
        double Distance(Joint j1, Joint j2) => Math.Sqrt(
            Math.Pow(j1.Position.X - j2.Position.X, 2) +
            Math.Pow(j1.Position.Y - j2.Position.Y, 2) +
            Math.Pow(j1.Position.Z - j2.Position.Z, 2));

        double torsoHeight = Distance(body.Joints[JointType.SpineBase], body.Joints[JointType.SpineMid]) +
                             Distance(body.Joints[JointType.SpineMid], body.Joints[JointType.Neck]) +
                             Distance(body.Joints[JointType.Neck], body.Joints[JointType.Head]);

        double leftLeg = Distance(body.Joints[JointType.HipLeft], body.Joints[JointType.KneeLeft]) +
                         Distance(body.Joints[JointType.KneeLeft], body.Joints[JointType.AnkleLeft]) +
                         Distance(body.Joints[JointType.AnkleLeft], body.Joints[JointType.FootLeft]);

        double rightLeg = Distance(body.Joints[JointType.HipRight], body.Joints[JointType.KneeRight]) +
                          Distance(body.Joints[JointType.KneeRight], body.Joints[JointType.AnkleRight]) +
                          Distance(body.Joints[JointType.AnkleRight], body.Joints[JointType.FootRight]);

        // Calculate the average of both legs and add to torso height
        double calculatedHeight = torsoHeight + ((leftLeg + rightLeg) / 2.0);
        
        // You can use either the calculated height or the fixed value (1.56)
        // Uncomment the line below to always return 1.56 regardless of calculation
        // return 1.56;
        
        return calculatedHeight;
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
        _skeletalTimer?.Dispose();
        _bodyFrameReader?.Dispose();
        _sensor?.Close();
        _httpListener?.Stop();
        _httpListener?.Close();
    }
}
