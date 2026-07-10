# Bigpdf

A feature-rich web application for PDF processing and manipulation built with .NET 10 and Blazor.

## 🎯 Features

- **PDF Compression** - Reduce PDF file sizes while maintaining quality
- **PDF Conversion** - Convert PDFs to various formats (images, etc.)
- **Office Document Conversion** - Convert Office documents to PDF
- **OCR (Optical Character Recognition)** - Extract text from scanned PDFs
- **Merge PDFs** - Combine multiple PDF files into one
- **Split PDFs** - Divide PDF files by page ranges
- **Add Watermarks** - Add text or image watermarks to PDFs
- **Convert PDF to JPG** - Extract pages as image files
- **Page Numbering** - Add page numbers to PDF documents
- **Job Tracking** - Monitor and manage processing jobs
- **User Authentication** - Secure login system for users
- **Workspace Management** - Organize and manage uploaded files

## 🛠️ Tech Stack

- **Framework**: .NET 10 with Blazor (WebAssembly)
- **Language**: C#
- **UI**: Razor Components
- **Authentication**: Built-in login/authorization system
- **Background Processing**: Background task queue for async operations

## 📁 Project Structure

```
Bigpdf/
├── Components/              # Blazor components
│   ├── Pages/              # Application pages (Compress, Convert, OCR, etc.)
│   ├── Layout/             # UI layout components
│   ├── Shared/             # Shared components
│   └── App.razor           # Root component
├── Services/               # Business logic services
│   ├── PdfService.cs       # Main PDF operations service
│   ├── JobService.cs       # Job management service
│   ├── JobStore.cs         # Job persistence
│   ├── BackgroundWorker.cs # Background task processor
│   └── UploadPaths.cs      # File upload management
├── Models/                 # Data models
│   ├── JobInfo.cs
│   ├── JobRequest.cs
│   ├── PdfOperationResult.cs
│   └── UploadResult.cs
├── Properties/             # Project properties
├── wwwroot/               # Static web assets
├── uploads/               # Uploaded file storage
├── data/                  # Application data directory
└── Program.cs             # Application entry point
```

## 🚀 Getting Started

### Prerequisites

- .NET 10 SDK or later
- Windows, macOS, or Linux

### Installation

1. Clone the repository:
   ```bash
   git clone <repository-url>
   cd Bigpdf
   ```

2. Restore NuGet packages:
   ```bash
   dotnet restore
   ```

3. Build the project:
   ```bash
   dotnet build
   ```

### Running the Application

#### Development Mode
```bash
dotnet run --configuration Debug
```

The application will start at `https://localhost:5001` (or the configured port).

#### Production Mode
```bash
dotnet run --configuration Release
```

## ⚙️ Configuration

Configuration is managed through `appsettings.json` and `appsettings.Development.json`:

- **Development Settings** (`appsettings.Development.json`): Used during development with relaxed constraints
- **Production Settings** (`appsettings.json`): Production configuration with security best practices

## 🏗️ Architecture Overview

### Services Layer

- **IPdfService**: Main interface for PDF operations
- **IJobService**: Manages job creation and tracking
- **IBackgroundTaskQueue**: Handles asynchronous job processing
- **IJobStore**: Persists job information

### Key Components

- **Program.cs**: Configures dependency injection, authentication, and middleware
- **App.razor**: Root Blazor component with routing
- **MainLayout.razor**: Main application layout with navigation
- **JobTracker.razor**: Real-time job status tracking

### Data Models

- **JobRequest**: Represents a PDF processing request
- **JobInfo**: Contains job metadata and status
- **PdfOperationResult**: Result of a PDF operation
- **UploadResult**: Result of file upload operations

## 📦 API Endpoints

The application exposes REST endpoints for:
- PDF processing operations
- Job status queries
- File uploads
- File downloads

See `ApiEndpoints.json` (generated during build) for the complete endpoint list.

## 🔐 Security

- User authentication system prevents unauthorized access
- RedirectToLogin component ensures protected routes
- File uploads are stored in controlled directory (`/uploads`)

## 🔄 Background Processing

Jobs are processed asynchronously using:
- **BackgroundTaskQueue**: Queues processing tasks
- **BackgroundWorker**: Processes queued tasks in the background

This allows the UI to remain responsive while PDFs are being processed.

## 📝 Development

### Adding a New PDF Operation

1. Create a new page component in `Components/Pages/`
2. Add the operation method to `IPdfService` and `PdfService`
3. Register the new route in `Routes.razor`
4. Add the operation to the navigation menu

### Adding a New Service

1. Define the interface in `Services/`
2. Implement the service class
3. Register in `Program.cs` dependency injection container

## 🧪 Building and Testing

```bash
# Clean build
dotnet clean
dotnet build

# Run with specific configuration
dotnet run --configuration Debug
```

## 📋 License

[Specify your license here]

## 🤝 Contributing

[Contribution guidelines here]

## 📧 Support

[Contact or support information here]
