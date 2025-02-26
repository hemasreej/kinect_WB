using Microsoft.Kinect;
using Firebase.Database;
using Firebase.Database.Query;
using System;
using System.Linq;
using System.Net;
using System.Text;
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
            _httpListener.Prefixes.Add("http://localhost:5001/startSkeletal/");  // New skeletal tracking route
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
                        else if (context.Request.Url.AbsolutePath == "/startSkeletal/")  // Handle skeletal tracking
                        {
                            StartTrackingSkeletal();
                            responseString = "{\"status\": \"Skeletal tracking started\"}";
                        }

                        byte[] buffer = Encoding.UTF8.GetBytes(responseString);
                        response.ContentLength64 = buffer.Length;
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
        if (_isMeasuringHeight)
        {
            Console.WriteLine("⚠ Height capturing is already running.");
            return;
        }

        _isMeasuringHeight = true;
        _isTrackingSkeletal = false; // Stop skeletal tracking if it's running
        Console.WriteLine("📏 Kinect Height Measurement Started...");
    }

    public void StartTrackingSkeletal()
    {
        if (_isTrackingSkeletal)
        {
            Console.WriteLine("⚠ Skeletal tracking is already running.");
            return;
        }

        _isTrackingSkeletal = true;
        _isMeasuringHeight = false; // Stop height tracking if it's running
        Console.WriteLine("🦴 Skeletal Tracking Started...");
    }

    private async void BodyFrameArrived(object? sender, BodyFrameArrivedEventArgs e)
    {
        using (var bodyFrame = e.FrameReference.AcquireFrame())
        {
            if (bodyFrame == null) return;

            Body[] bodies = new Body[bodyFrame.BodyCount];
            bodyFrame.GetAndRefreshBodyData(bodies);

            foreach (var body in bodies.Where(b => b.IsTracked))
            {
                if (_isMeasuringHeight)
                {
                    double height = CalculateHeight(body);
                    Console.WriteLine($"📏 Height: {height:F2} meters");

                    await SaveHeightToFirebase(height);
                    _isMeasuringHeight = false; // Stop measuring after one reading
                }
                else if (_isTrackingSkeletal)
                {
                    var jointData = ExtractJointPositions(body);
                    Console.WriteLine("🦴 Skeletal Data Captured");

                    await SaveSkeletalDataToFirebase(jointData);
                }

                break; // Process only one body
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

        return torsoHeight + ((leftLeg + rightLeg) / 2.0);
    }

    private async Task SaveHeightToFirebase(double height)
    {
        await _firebaseClient.Child("patients").Child(_patientId).Child("height").PutAsync(height.ToString("F2"));
        Console.WriteLine($"✅ Height updated for {_patientId}: {height:F2} meters");
    }
    private string GetHeightJson()
    {
        return $"{{\"status\": \"{(_isMeasuringHeight ? "Measuring" : "Idle")}\"}}";
    }


    private object ExtractJointPositions(Body body)
    {
        return body.Joints.ToDictionary(j => j.Key.ToString(), j => new
        {
            X = j.Value.Position.X,
            Y = j.Value.Position.Y,
            Z = j.Value.Position.Z
        });
    }

    private async Task SaveSkeletalDataToFirebase(object jointData)
    {
        await _firebaseClient.Child("patients").Child(_patientId).Child("skeletal").PutAsync(jointData);
        Console.WriteLine($"✅ Skeletal data updated for {_patientId}");
    }

    public string Stop()
    {
        _isMeasuringHeight = false;
        _isTrackingSkeletal = false;
        Console.WriteLine("🛑 Stopped all Kinect tracking.");
        return "{\"status\": \"Tracking stopped\"}";
    }
}