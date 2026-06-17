# 🚁 DivyaLink Ground Control Station

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat&logo=dotnet)](https://dotnet.microsoft.com/)
[![Blazor](https://img.shields.io/badge/Blazor-Server-512BD4?style=flat&logo=blazor)](https://dotnet.microsoft.com/apps/aspnet/web-apps/blazor)
[![MAVLink](https://img.shields.io/badge/MAVLink-2.0-00ADD8?style=flat)](https://mavlink.io/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A modern, real-time Ground Control Station (GCS) for UAV/Drone operations built with Blazor Server and MAVLink protocol. DivyaLink provides comprehensive telemetry monitoring, mission planning, swarm management, and live video streaming capabilities.

![DivyaLink](docs/screenshot.png)

---

## ✨ Features

### 🎯 Core Capabilities
- **Real-time Telemetry Monitoring** - Live tracking of GPS, altitude, attitude, speed, battery, and system health
- **Interactive Mission Planning** - Visual waypoint creation and editing with drag-and-drop support
- **Multi-Protocol Connectivity** - Support for Serial (USB), UDP, and TCP/IP connections
- **Swarm Management** - Monitor and control multiple drones simultaneously
- **Live Video Streaming** - WebRTC-based video feed integration via MediaMTX
- **Flight Control** - Arm/disarm, mode changes, takeoff, land, RTL, and guided mode commands
- **Pre-flight Checklist** - Comprehensive system health verification
- **Sensor Calibration** - Built-in tools for compass, accelerometer, and gyroscope calibration
- **RC Channel Monitoring** - Real-time visualization of transmitter inputs
- **Toast Notifications** - Smart event-based alerts for arm/disarm, mode changes, and connection status

### 🗺️ Map Features
- **Interactive Leaflet Map** - Dark-themed satellite imagery from Bing Maps
- **Live Drone Position** - Heading-oriented drone icon with real-time updates
- **Flight Trail** - Visual breadcrumb trail (configurable, up to 400 points)
- **GPS Accuracy Visualization** - HDOP-based accuracy circle
- **Mission Overlay** - Visual waypoint markers with path lines
- **Fullscreen Mode** - Expand map for better situational awareness
- **Drag-to-Reposition** - Move waypoints directly on the map

### 📡 MAVLink Integration
- **Full ArduPilot Support** - Complete command set for ArduCopter/ArduPlane
- **Parameter Management** - Read/write flight controller parameters
- **Heartbeat Monitoring** - Connection stability tracking with automatic reconnect
- **RC Failsafe Detection** - Intelligent detection of transmitter connection loss
- **Multi-drone Support** - System ID-based swarm management
- **High-frequency Telemetry** - 10Hz UI updates with packet rate monitoring

---

## 🏗️ Architecture

### Technology Stack

**Frontend**
- **Blazor Server** - Interactive server-side rendering with SignalR
- **Tailwind CSS** - Utility-first styling with custom dark theme
- **Leaflet.js** - Interactive mapping with custom drone markers
- **Bootstrap Icons** - Comprehensive icon set

**Backend**
- **.NET 10.0** - Modern C# with nullable reference types
- **MAVLink Library** - Protocol parsing and message generation
- **Background Services** - Dedicated telemetry processing thread
- **SignalR** - Real-time bidirectional communication

**Video Streaming**
- **WebRTC** - Low-latency peer-to-peer video
- **MediaMTX** - RTSP to WebRTC bridge
- **WHEP Protocol** - Standards-based WebRTC ingestion

### Project Structure

```
DivyaLink/
├── Components/
│   ├── Layout/
│   │   └── MainLayout.razor          # Main application shell
│   ├── Pages/
│   │   ├── Home.razor                # Map view with telemetry overlay
│   │   ├── FlightPlan.razor          # Mission planning interface
│   │   ├── Quick.razor               # Quick actions panel
│   │   ├── Hud.razor                 # Heads-up display
│   │   ├── Setup.razor               # Sensor calibration
│   │   ├── PreFlight.razor           # Pre-flight checklist
│   │   ├── Messages.razor            # MAVLink message log
│   │   ├── Swarm.razor               # Multi-drone dashboard
│   │   └── Actions.razor             # Flight control commands
│   └── UI/
│       ├── ConnectionDialog.razor    # Connection configuration
│       └── ToastContainer.razor      # Notification system
├── Services/
│   └── MavlinkService.cs             # Core MAVLink communication service
├── Models/
│   ├── DroneState.cs                 # Telemetry data model
│   └── WaypointModel.cs              # Mission waypoint structure
├── wwwroot/
│   ├── app.css                       # Compiled Tailwind styles
│   └── app.tailwind.css              # Tailwind source
├── appsettings.json                  # Configuration (production)
├── appsettings.Development.json      # Configuration (development)
├── Program.cs                        # Application entry point
└── DivyaLink.csproj                 # Project file
```

---

## 🚀 Getting Started

### Prerequisites

- **.NET 10.0 SDK** - [Download](https://dotnet.microsoft.com/download/dotnet/10.0)
- **Node.js** (for Tailwind CSS compilation) - [Download](https://nodejs.org/)
- **MAVLink-compatible Drone** - ArduPilot (Copter/Plane) or PX4
- **(Optional) MediaMTX** - For video streaming - [Download](https://github.com/bluenviron/mediamtx)

### Installation

1. **Clone the repository**
   ```bash
   git clone https://github.com/yourusername/divyalink-gcs.git
   cd divyalink-gcs
   ```

2. **Restore dependencies**
   ```bash
   dotnet restore
   ```

3. **Build the project**
   ```bash
   dotnet build
   ```

4. **Run the application**
   ```bash
   dotnet run
   ```

5. **Access the GCS**
   - Open your browser to `https://localhost:5001`
   - The application will start on the default Kestrel port

### Configuration

Edit `appsettings.json` to configure connection defaults:

```json
{
  "TcpConnection": {
    "DefaultHost": "192.168.144.10",
    "DefaultPort": 5762,
    "ConnectTimeoutSeconds": 5,
    "ReadTimeoutSeconds": 30,
    "EnableKeepAlive": true,
    "ReconnectDelaySeconds": 3,
    "MaxReconnectAttempts": 5
  },
  "VideoStreaming": {
    "Enabled": true,
    "MediaMTXUrl": "http://192.168.144.10:8889",
    "RTSPStreamUrl": "rtsp://192.168.144.25:8554/main.264",
    "VideoWidth": 320,
    "VideoPosition": "bottom-right"
  },
  "GoogleMaps": {
    "ApiKey": "YOUR_GOOGLE_MAPS_API_KEY"
  }
}
```

---

## 📖 Usage Guide

### Connecting to a Drone

1. **Click the connection icon** in the top navigation bar
2. **Select connection type:**
   - **Serial (USB)** - Direct USB connection to flight controller
   - **UDP** - Network connection (e.g., WiFi telemetry)
   - **TCP/IP** - Mission Planner SITL or network proxy
3. **Enter connection details:**
   - Serial: COM port and baud rate (default: 115200)
   - UDP: Listen port (default: 14550)
   - TCP: Host IP and port (default: 192.168.144.10:5762)
4. **Click Connect**

The connection status indicator will turn green when connected, and telemetry will begin streaming.

### Mission Planning

1. **Navigate to the Flight Plan tab**
2. **Create waypoints:**
   - Click on the map to add waypoints
   - Or use the "Add Waypoint" button for manual entry
3. **Edit waypoints:**
   - Drag waypoints on the map to reposition
   - Edit altitude, coordinates, or commands in the table
4. **Set home position** - Right-click a waypoint and select "Set as Home"
5. **Upload mission:**
   - Click "Upload to Drone"
   - Wait for confirmation toast
6. **Save/Load missions:**
   - Export to JSON for backup
   - Import previously saved missions

### Flight Operations

**Pre-Flight Checklist**
- Navigate to the Pre-Flight tab
- Verify all systems show green status
- Check GPS lock (minimum 8 satellites recommended)
- Verify battery voltage and capacity
- Ensure RC transmitter is connected

**Taking Off**
1. Ensure the drone is in a safe location
2. Click "Arm" in the Quick Actions panel
3. Wait for arming confirmation toast
4. Click "Takeoff" and enter desired altitude
5. Monitor telemetry overlay for climb rate

**Landing**
- Click "Land" for automated landing at current position
- Or click "RTL" (Return to Launch) to return home first

**Emergency Stop**
- Click "Disarm" to immediately cut motors (use only when on the ground)

### Sensor Calibration

**Compass Calibration**
1. Navigate to Setup → Calibrate Compass
2. Click "Start Compass Calibration"
3. Rotate the drone slowly in all axes as instructed
4. Wait for "Calibration Complete" toast

**Accelerometer Calibration**
1. Navigate to Setup → Calibrate Accel
2. Follow on-screen instructions for each position
3. Place drone flat, on side, nose up, etc. as prompted
4. Wait for completion

---

## 🔧 Development

### Building from Source

```bash
# Clone the repository
git clone https://github.com/yourusername/divyalink-gcs.git
cd divyalink-gcs

# Restore NuGet packages
dotnet restore

# Build Tailwind CSS
npx @tailwindcss/cli -i ./wwwroot/app.tailwind.css -o ./wwwroot/app.css

# Build the project
dotnet build

# Run in development mode
dotnet run --launch-profile https
```

### Dependencies

```xml
<PackageReference Include="Asv.Mavlink" Version="4.0.18" />
<PackageReference Include="MAVLink" Version="1.0.8" />
<PackageReference Include="MavSdk.Net" Version="1.1.3" />
<PackageReference Include="System.Management" Version="10.0.3" />
```

### Extending Functionality

**Adding New MAVLink Commands**

1. Open `MavlinkService.cs`
2. Add a new public method:
   ```csharp
   public async Task<bool> SendCustomCommand(params)
   {
       var msg = new msg_command_long
       {
           target_system = PrimarySysId,
           target_component = 1,
           command = (ushort)MAVLink.MAV_CMD.YOUR_COMMAND,
           // ... set parameters
       };
       return await SendCommandAsync(msg);
   }
   ```
3. Add UI button in the appropriate Razor component

**Customizing Telemetry Display**

Edit `Home.razor` to add new telemetry overlays:
```razor
<div class="@statBoxBase">
    <div class="text-[1.2rem]">🎯</div>
    <div>
        <div class="@statLabel">Your Metric</div>
        <div class="@statVal">@Drone.State.YourValue</div>
    </div>
</div>
```

---

## 🎨 Customization

### Theme Colors

The application uses a dark theme optimized for outdoor use. To customize colors, edit the Tailwind configuration or CSS variables:

```css
:root {
    --primary: #00f2ff;      /* Cyan accent */
    --warning: #f0b429;      /* Amber warnings */
    --danger: #ff3a3a;       /* Red alerts */
    --background: #0a0b0d;   /* Dark background */
    --glass: rgba(10,11,13,0.85);  /* Glass morphism */
}
```

### Video Streaming Configuration

**MediaMTX Setup**
1. Download and run MediaMTX
2. Configure RTSP input from your drone camera
3. Update `appsettings.json` with the MediaMTX WHEP endpoint
4. Enable video in configuration: `"Enabled": true`

**Supported Video Positions**
- `top-left`
- `top-right`
- `bottom-left`
- `bottom-right`

---

## 🐛 Troubleshooting

### Connection Issues

**Problem:** "Connection failed" toast appears
- **Solution:** Verify the drone is powered on and MAVLink is enabled
- Check firewall settings (allow UDP port 14550 or your TCP port)
- Ensure correct baud rate for serial connections (usually 57600 or 115200)

**Problem:** Connection drops frequently
- **Solution:** Check USB cable quality for serial connections
- Reduce wireless interference for UDP/WiFi telemetry
- Enable keepalive in `appsettings.json`

### Telemetry Issues

**Problem:** No GPS position on map
- **Solution:** Ensure GPS has 3D fix (check satellite count > 6)
- Wait for GPS initialization (can take 30-60 seconds outdoors)
- Check GPS antenna is not obstructed

**Problem:** Altitude shows 0 or incorrect values
- **Solution:** Verify barometer calibration
- Set home position after GPS lock
- Check `GLOBAL_POSITION_INT` messages in Messages tab

### Video Streaming

**Problem:** Video not displaying
- **Solution:** Verify MediaMTX is running on the configured port
- Check RTSP stream URL is correct
- Ensure browser supports WebRTC (Chrome/Edge recommended)
- Check browser console for WebRTC errors

**Problem:** High video latency
- **Solution:** Reduce video resolution in camera settings
- Use wired connection instead of WiFi when possible
- Check network bandwidth

---

## 🤝 Contributing

Contributions are welcome! Please follow these guidelines:

1. **Fork the repository**
2. **Create a feature branch** (`git checkout -b feature/amazing-feature`)
3. **Commit your changes** (`git commit -m 'Add amazing feature'`)
4. **Push to the branch** (`git push origin feature/amazing-feature`)
5. **Open a Pull Request**

### Code Style
- Use C# nullable reference types
- Follow standard .NET naming conventions
- Add XML documentation for public APIs
- Use async/await for I/O operations
- Keep Razor components focused and single-purpose

---

## 📝 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

---

## 🙏 Acknowledgments

- **MAVLink Project** - For the robust UAV communication protocol
- **ArduPilot Team** - For the open-source autopilot platform
- **Leaflet.js** - For the powerful mapping library
- **Blazor Team** - For the modern web framework
- **MediaMTX** - For WebRTC streaming capabilities

---

## 📞 Support

- **Issues:** [GitHub Issues](https://github.com/yourusername/divyalink-gcs/issues)
- **Discussions:** [GitHub Discussions](https://github.com/yourusername/divyalink-gcs/discussions)
- **Email:** support@divyalink.com

---

## 🗺️ Roadmap

- [ ] Multi-language support (i18n)
- [ ] Offline map tiles caching
- [ ] Flight data logging and analysis
- [ ] Geofencing with boundary alerts
- [ ] Advanced swarm choreography
- [ ] Mobile app (iOS/Android)
- [ ] Parameter file backup/restore
- [ ] Custom HUD layouts
- [ ] Rally point management
- [ ] Terrain following visualization

---

## 📊 System Requirements

**Minimum**
- CPU: Dual-core 2.0 GHz
- RAM: 4 GB
- GPU: Integrated graphics
- OS: Windows 10, Linux (Ubuntu 20.04+), macOS 11+
- Browser: Chrome 90+, Edge 90+, Firefox 88+

**Recommended**
- CPU: Quad-core 2.5 GHz
- RAM: 8 GB
- GPU: Dedicated graphics for smoother map rendering
- Network: Stable connection for video streaming
- Display: 1920x1080 or higher resolution

---

<div align="center">

**Built with ❤️ for the drone community**

[⭐ Star this repo](https://github.com/yourusername/divyalink-gcs) • [🐛 Report Bug](https://github.com/yourusername/divyalink-gcs/issues) • [✨ Request Feature](https://github.com/yourusername/divyalink-gcs/issues)

</div>
