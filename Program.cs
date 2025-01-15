using System.Device.Gpio;
using System.Device.I2c;
using Iot.Device.Bmxx80;
using Iot.Device.Bmxx80.ReadResult;
using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Shared;
using System.Text;

namespace CheeseCaveDotnet;

/// <summary>
/// Represents the device used in the Cheese Cave monitoring system.
/// </summary>
class Device
{    
    private static readonly int s_pin = 21; // The GPIO pin number used for the device.
    private static GpioController s_gpio; // The GPIO controller used to interact with the GPIO pins.
    private static I2cDevice s_i2cDevice; // The I2C device used for communication with the BME280 sensor.
    private static Bme280 s_bme280; // The BME280 sensor used to measure temperature, humidity, and pressure.

    const double DesiredTempLimit = 5;          // Acceptable range above or below the desired temp, in degrees F.
    const double DesiredHumidityLimit = 10;     // Acceptable range above or below the desired humidity, in percentages.
    const int IntervalInMilliseconds = 5000;    // Interval at which telemetry is sent to the cloud.

    private static DeviceClient s_deviceClient; // The client used to communicate with the Azure IoT Hub.
    private static stateEnum s_fanState = stateEnum.off; // The current state of the fan.                      

    private static readonly string s_deviceConnectionString = "YOUR DEVICE CONNECTION STRING HERE"; // The connection string used to connect to the Azure IoT Hub.

    enum stateEnum // Represents the possible states of the fan.
    {
        off,
        on,
        failed
    }

    /// <summary>
    /// The entry point of the application.
    /// Initializes the GPIO controller, I2C device, and BME280 sensor.
    /// Displays a startup message.
    /// Creates a device client for communication with Azure IoT Hub using MQTT protocol.
    /// Sets a method handler for updating the fan state.
    /// Starts monitoring conditions and updating the device twin asynchronously.
    /// Waits for user input before closing the GPIO pin.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    private static void Main(string[] args)
    {
        // Initialize the GPIO controller.
        s_gpio = new GpioController();
        // Open the GPIO pin for output.
        s_gpio.OpenPin(s_pin, PinMode.Output);

        // Set up the I2C connection settings for the BME280 sensor.
        var i2cSettings = new I2cConnectionSettings(1, Bme280.DefaultI2cAddress);
        // Create the I2C device.
        s_i2cDevice = I2cDevice.Create(i2cSettings);

        // Initialize the BME280 sensor with the I2C device.
        s_bme280 = new Bme280(s_i2cDevice);

        // Display a startup message in yellow.
        ColorMessage("Cheese Cave device app.\n", ConsoleColor.Yellow);

        // Create a device client for communication with Azure IoT Hub using MQTT protocol.
        s_deviceClient = DeviceClient.CreateFromConnectionString(s_deviceConnectionString, TransportType.Mqtt);

        // Set a method handler for updating the fan state.
        s_deviceClient.SetMethodHandlerAsync("SetFanState", SetFanState, null).Wait();

        // Start monitoring conditions and updating the device twin asynchronously.
        MonitorConditionsAndUpdateTwinAsync();

        // Wait for user input before closing the GPIO pin.
        Console.ReadLine();
        // Close the GPIO pin.
        s_gpio.ClosePin(s_pin);
    }

    /// <summary>
    /// Monitors the conditions and updates the device twin properties asynchronously.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private static async void MonitorConditionsAndUpdateTwinAsync()
    {
        while (true)
        {
            // Read sensor data from the BME280 sensor.
            Bme280ReadResult sensorOutput = s_bme280.Read();         

            // Update the device twin properties with the current temperature and humidity.
            await UpdateTwin(
                    sensorOutput.Temperature.Value.DegreesFahrenheit, 
                    sensorOutput.Humidity.Value.Percent);

            // Wait for the specified interval before reading the sensor data again.
            await Task.Delay(IntervalInMilliseconds);
        }
    }

    /// <summary>
    /// Handles the direct method call to set the fan state.
    /// </summary>
    /// <param name="methodRequest">The method request containing the desired fan state.</param>
    /// <param name="userContext">The user context.</param>
    /// <returns>A task that represents the response to the direct method call.</returns>
    private static Task<MethodResponse> SetFanState(MethodRequest methodRequest, object userContext)
    {
        // Check if the fan is in a failed state.
        if (s_fanState is stateEnum.failed)
        {
            // Create a result message indicating the fan has failed.
            string result = "{\"result\":\"Fan failed\"}";
            // Log the failure message in red.
            RedMessage("Direct method failed: " + result);
            // Return a method response with a 400 status code.
            return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(result), 400));
        }
        else
        {
            try
            {
                // Decode the method request data.
                var data = Encoding.UTF8.GetString(methodRequest.Data);

                // Remove any quotes from the data.
                data = data.Replace("\"", "");

                // Parse the data to set the fan state.
                s_fanState = (stateEnum)Enum.Parse(typeof(stateEnum), data);
                // Log the new fan state in green.
                GreenMessage("Fan set to: " + data);

                // Write the new state to the GPIO pin.
                s_gpio.Write(s_pin, s_fanState == stateEnum.on ? PinValue.High : PinValue.Low);

                // Create a result message indicating successful execution.
                string result = "{\"result\":\"Executed direct method: " + methodRequest.Name + "\"}";
                // Return a method response with a 200 status code.
                return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(result), 200));
            }
            catch
            {
                // Create a result message indicating an invalid parameter.
                string result = "{\"result\":\"Invalid parameter\"}";
                // Log the failure message in red.
                RedMessage("Direct method failed: " + result);
                // Return a method response with a 400 status code.
                return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(result), 400));
            }
        }
    }

    /// <summary>
    /// Updates the device twin properties with the current state of the device.
    /// </summary>
    private static async Task UpdateTwin(double currentTemperature, double currentHumidity)
    {
        // Create a new TwinCollection to hold the reported properties.
        var reportedProperties = new TwinCollection();
        
        // Add the current fan state to the reported properties.
        reportedProperties["fanstate"] = s_fanState.ToString();
        
        // Add the current humidity to the reported properties, rounded to 2 decimal places.
        reportedProperties["humidity"] = Math.Round(currentHumidity, 2);
        
        // Add the current temperature to the reported properties, rounded to 2 decimal places.
        reportedProperties["temperature"] = Math.Round(currentTemperature, 2);
        
        // Update the reported properties of the device twin.
        await s_deviceClient.UpdateReportedPropertiesAsync(reportedProperties);

        // Log the reported twin state in green.
        GreenMessage("Twin state reported: " + reportedProperties.ToJson());
    }

    /// <summary>
    /// Colors the message text based on the severity level.
    /// </summary>
    /// <param name="message">The message to color.</param>
    /// <param name="severity">The severity level of the message.</param>
    /// <returns>The colored message text.</returns>
    private static void ColorMessage(string text, ConsoleColor clr)
    {
        // Set the console text color to the specified color.
        Console.ForegroundColor = clr;
        // Write the message text to the console.
        Console.WriteLine(text);
        // Reset the console text color to the default color.
        Console.ResetColor();
    }
    
    /// <summary>
    /// Colors the message text green.
    /// </summary>
    /// <param name="message">The message to color green.</param>
    /// <returns>The green-colored message text.</returns>
    private static void GreenMessage(string text) => 
        // Call the ColorMessage method with the specified text and green color.
        ColorMessage(text, ConsoleColor.Green);

    /// <summary>
    /// Colors the message text red.
    /// </summary>
    /// <param name="message">The message to color red.</param>
    /// <returns>The red-colored message text.</returns>
    private static void RedMessage(string text) => 
        // Call the ColorMessage method with the specified text and red color.
        ColorMessage(text, ConsoleColor.Red);
}
