using System;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        string firebaseUrl = "https://databasec-b35a7-default-rtdb.firebaseio.com/"; // Replace with your actual Firebase URL
        string patientId = "temp_patient";  // This will be updated dynamically

        try
        {
            KinectHelper kinect = new KinectHelper(firebaseUrl, patientId);

            // Step 1: Start height measurement
            kinect.StartTrackingHeight();
            Console.WriteLine("📏 Measuring height...");
            await Task.Delay(5000); // Wait for height measurement to complete (adjust as needed)

            // Step 2: Start skeletal tracking automatically
            kinect.StartTrackingSkeletal();
            Console.WriteLine("🦴 Skeletal tracking started...");

            // Keep the application running
            await Task.Delay(-1);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error initializing Kinect: {ex.Message}");
        }
    }
}