using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        // Proxy configuration
        int proxyPort = 8888;  // Port the proxy listens on
        
        // Initialize logger
        var logger = new ProxyLogger(
            logPath: "proxy.log",
            metricsPath: "proxy_metrics.csv"
        );
        
        // Create the proxy server
        TcpListener listener = new TcpListener(IPAddress.Any, proxyPort);
        listener.Start();
        Console.WriteLine($"Proxy started on port {proxyPort}");

        while (true)
        {
            try
            {
                // Accept client connections
                TcpClient clientConnection = await listener.AcceptTcpClientAsync();
                _ = HandleClientAsync(clientConnection, logger);
            }
            catch (Exception ex)
            {
                await logger.LogError("Error accepting client", ex);
                Console.WriteLine($"Error accepting client: {ex.Message}");
            }
        }
    }

    static async Task HandleClientAsync(TcpClient clientConnection, ProxyLogger logger)
    {
        var metrics = new ProxyLogger.RequestMetrics
        {
            StartTime = DateTime.UtcNow
        };

        try
        {
            using (clientConnection)
            {
                NetworkStream clientStream = clientConnection.GetStream();
                StreamReader reader = new StreamReader(clientStream, Encoding.ASCII, leaveOpen: true);

                // Read the first line of the HTTP request
                string requestLine = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(requestLine))
                    return;

                // Parse the request line
                string[] requestParts = requestLine.Split(' ');
                if (requestParts.Length != 3)
                    return;

                metrics.Method = requestParts[0];
                metrics.Url = requestParts[1];

                if (metrics.Method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    // Handle HTTPS connection
                    await HandleHttpsConnection(clientConnection, metrics.Url, reader);
                    metrics.StatusCode = 200; // Connection Established
                    metrics.EndTime = DateTime.UtcNow;
                    await logger.LogRequest(metrics);
                    return;
                }

                // Parse the URL to get host and port
                Uri uri = new Uri(metrics.Url.StartsWith("http://") ? metrics.Url : "http://" + metrics.Url);
                string targetHost = uri.Host;
                int targetPort = uri.Port;

                Console.WriteLine($"Forwarding request to: {targetHost}:{targetPort}");

                using (TcpClient targetConnection = new TcpClient())
                {
                    // Connect to target server
                    await targetConnection.ConnectAsync(targetHost, targetPort);
                    NetworkStream targetStream = targetConnection.GetStream();

                    // Reconstruct and forward the request
                    string modifiedRequest = $"{metrics.Method} {uri.PathAndQuery} HTTP/1.1\r\n";
                    byte[] requestData = Encoding.ASCII.GetBytes(modifiedRequest);
                    await targetStream.WriteAsync(requestData, 0, requestData.Length);

                    // Forward the rest of the request headers
                    string line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                    {
                        string header = line + "\r\n";
                        requestData = Encoding.ASCII.GetBytes(header);
                        await targetStream.WriteAsync(requestData, 0, requestData.Length);
                    }
                    // End of headers
                    await targetStream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), 0, 2);

                    // Forward any request body if present
                    if (clientConnection.Available > 0)
                    {
                        await ForwardDataAsync(clientStream, targetStream);
                    }

                    // Read the response status line to get the status code
                    byte[] responseBuffer = new byte[8192];
                    int bytesRead = await targetStream.ReadAsync(responseBuffer, 0, responseBuffer.Length);
                    string responseStart = Encoding.ASCII.GetString(responseBuffer, 0, bytesRead);
                    
                    // Parse status code from response
                    var statusLine = responseStart.Split('\n')[0];
                    if (statusLine.StartsWith("HTTP/"))
                    {
                        metrics.StatusCode = int.Parse(statusLine.Split(' ')[1]);
                    }

                    // Write the response back to the client
                    await clientStream.WriteAsync(responseBuffer, 0, bytesRead);

                    // Forward the rest of the response
                    metrics.ContentLength = bytesRead;
                    while ((bytesRead = await targetStream.ReadAsync(responseBuffer, 0, responseBuffer.Length)) > 0)
                    {
                        await clientStream.WriteAsync(responseBuffer, 0, bytesRead);
                        metrics.ContentLength += bytesRead;
                    }
                }

                metrics.EndTime = DateTime.UtcNow;
                await logger.LogRequest(metrics);
            }
        }
        catch (Exception ex)
        {
            metrics.EndTime = DateTime.UtcNow;
            metrics.StatusCode = 500; // Internal Server Error
            await logger.LogRequest(metrics);
            await logger.LogError("Error handling client", ex);
            Console.WriteLine($"Error handling client: {ex.Message}");
        }
    }

    static async Task HandleHttpsConnection(TcpClient clientConnection, string url, StreamReader reader)
    {
        // Parse host and port from CONNECT request
        string[] hostParts = url.Split(':');
        string targetHost = hostParts[0];
        int targetPort = hostParts.Length > 1 ? int.Parse(hostParts[1]) : 443;

        Console.WriteLine($"HTTPS Connection to: {targetHost}:{targetPort}");

        // Read and discard headers until empty line
        string line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) { }

        using (TcpClient targetConnection = new TcpClient())
        {
            try
            {
                // Connect to target server
                await targetConnection.ConnectAsync(targetHost, targetPort);

                // Send 200 Connection established to the client
                string response = "HTTP/1.1 200 Connection Established\r\n\r\n";
                byte[] responseBytes = Encoding.ASCII.GetBytes(response);
                await clientConnection.GetStream().WriteAsync(responseBytes, 0, responseBytes.Length);

                // Create bi-directional tunnel
                await Task.WhenAny(
                    ForwardDataAsync(clientConnection.GetStream(), targetConnection.GetStream()),
                    ForwardDataAsync(targetConnection.GetStream(), clientConnection.GetStream())
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in HTTPS tunnel: {ex.Message}");
                string errorResponse = $"HTTP/1.1 502 Bad Gateway\r\n\r\n";
                byte[] errorBytes = Encoding.ASCII.GetBytes(errorResponse);
                await clientConnection.GetStream().WriteAsync(errorBytes, 0, errorBytes.Length);
                throw; // Rethrow to be handled by the calling method's try-catch block
            }
        }
    }

    static async Task ForwardDataAsync(NetworkStream source, NetworkStream destination)
    {
        byte[] buffer = new byte[8192];
        try
        {
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await destination.WriteAsync(buffer, 0, bytesRead);
                await destination.FlushAsync();
            }
        }
        catch (IOException ex) when (ex.InnerException is SocketException)
        {
            // Connection closed by remote host - this is normal
            return;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error forwarding data: {ex.Message}");
            throw; // Rethrow to be handled by parent method's logging
        }
    }
}