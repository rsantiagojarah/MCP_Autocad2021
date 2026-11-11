"""Small synchronous MCP client for native integration tests, with bounded waits."""
import json
import queue
import subprocess
import threading


class Client:
    def __init__(self, dotnet, server, cwd):
        self.process = subprocess.Popen([str(dotnet), str(server)], cwd=cwd, stdin=subprocess.PIPE,
                                        stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                        text=True, encoding="utf-8", bufsize=1)
        self.responses = queue.Queue()
        self.errors = []
        self.sequence = 0
        threading.Thread(target=self._read, daemon=True).start()
        threading.Thread(target=lambda: self.errors.extend(self.process.stderr), daemon=True).start()
        self.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                    "clientInfo": {"name": "NativeTextHatchTests", "version": "0.5.0"}})
        self.process.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        self.process.stdin.flush()

    def _read(self):
        try:
            for line in self.process.stdout:
                self.responses.put(json.loads(line))
        except Exception as error:
            self.responses.put({"parseError": repr(error)})
        finally:
            self.responses.put({"closed": True})

    def request(self, method, parameters):
        self.sequence += 1
        self.process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": self.sequence,
                                            "method": method, "params": parameters}) + "\n")
        self.process.stdin.flush()
        while True:
            response = self.responses.get(timeout=45)
            assert not response.get("closed") and not response.get("parseError"), response
            if "id" not in response:
                continue
            assert response["id"] == self.sequence and "error" not in response, response
            return response["result"]

    def tool(self, name, arguments):
        result = self.request("tools/call", {"name": name, "arguments": arguments})
        return json.loads(result["content"][0]["text"])

    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.process.terminate()
            self.process.wait(timeout=5)
