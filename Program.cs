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
                _ = HandleClientAsync(clientConnection);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error accepting client: {ex.Message}");
            }
        }
    }

    static async Task HandleClientAsync(TcpClient clientConnection)
    {
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

                string method = requestParts[0];
                string url = requestParts[1];

                // Parse the URL to get host and port
                Uri uri = new Uri(url.StartsWith("http://") ? url : "http://" + url);
                string targetHost = uri.Host;
                int targetPort = uri.Port;

                Console.WriteLine($"Forwarding request to: {targetHost}:{targetPort}");

                using (TcpClient targetConnection = new TcpClient())
                {
                    // Connect to target server
                    await targetConnection.ConnectAsync(targetHost, targetPort);
                    NetworkStream targetStream = targetConnection.GetStream();

                    // Reconstruct and forward the request
                    string modifiedRequest = $"{method} {uri.PathAndQuery} HTTP/1.1\r\n";
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

                    // Forward the response back to client
                    await ForwardDataAsync(targetStream, clientStream);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling client: {ex.Message}");
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
    }
}
}