﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Net;
using System.Net.Sockets;

using Microsoft.Samples.Debugging.MdbgEngine;

namespace DotNetDbgp.ClientDebugger {
	public class Client {
		private readonly int _pid;

		private Socket _socket;
		private MDbgProcess _mdbgProcess;

		public Client(int pid) {
            _pid = pid;
		}

		public void Start() {
			var ip = IPAddress.Loopback;
			//var ip = Dns.GetHostEntry(Dns.GetHostName()).AddressList;
			var ipEndPoint = new IPEndPoint(ip, 9000);

			_socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			//_socket.DualMode = true;
			_socket.Connect(ipEndPoint);

			//new Thread(() => {
				this.Run();
			//}).Start();
		}

		public void Run() {
			try {
				var socketBuffer = new byte[4096];
				var messageBuffer = "";

				var engine = new MDbgEngine();
				_mdbgProcess = engine.Attach(_pid, MdbgVersionPolicy.GetDefaultAttachVersion(_pid));
				_mdbgProcess.AsyncStop().WaitOne();
				
				var sourcePosition = !_mdbgProcess.Threads.HaveActive ? null : _mdbgProcess.Threads.Active.CurrentSourcePosition;

				_socket.Send(Encoding.UTF8.GetBytes(this.GenerateOutputMessage(this.InitXml(sourcePosition != null ? sourcePosition.Path : null))));

				Console.CancelKeyPress += delegate {
					_mdbgProcess.AsyncStop().WaitOne();
					this.Stop();
					System.Environment.Exit(-1);
				};

				while(true) {
					var readLength = _socket.Receive(socketBuffer);
					if (readLength > 0) {
						messageBuffer += Encoding.UTF8.GetString(socketBuffer, 0, readLength);
					}
					if (readLength < 0) {
						throw new Exception("Receive failed");
					}

					while(messageBuffer.Contains("\0")) {
						var message = messageBuffer.Substring(0, messageBuffer.IndexOf('\0'));
						Console.WriteLine("Message: "+message);

						//Thread.Sleep(5000);
						//Console.WriteLine("Ok");
				
						messageBuffer = messageBuffer.Substring(message.Length+1);
						var parsedMessage = this.ParseInputMessage(message);

						Func<String,String,String> getParamOrDefault = (String key, String defaultVal) => {
							string val;
							parsedMessage.Item2.TryGetValue("-"+key, out val);
							val = val ?? defaultVal;
							return val;
						};

						var transId = getParamOrDefault("i", "");

						var command = parsedMessage.Item1;

						String outputMessage = null;
						switch(command) {
							case "detach":
								this.Stop();
								return;
							case "context_names":
								outputMessage = this.ContextNamesXml(transId);
								break;
							case "context_get": {
									var contextId = int.Parse(getParamOrDefault("c", "0"));
									var depth = int.Parse(getParamOrDefault("d", "0"));
									outputMessage = this.ContextGetXml(transId, contextId, depth);
								}
								break;
							case "property_get": {
									var contextId = int.Parse(getParamOrDefault("c", "0"));
									var name = getParamOrDefault("n", "");
									var depth = int.Parse(getParamOrDefault("d", "0"));
									outputMessage = this.PropertyGetXml(transId, contextId, name, depth);
								}
								break;
							case "run":
							case "step_into":
							case "step_over":
							case "step_out":
								//if (_mdbgProcess.Threads.HaveActive) {
									WaitHandle wait = null;
									while(!(_mdbgProcess.StopReason is StepCompleteStopReason || _mdbgProcess.StopReason is BreakpointHitStopReason)) {
										wait = _mdbgProcess.Go();
										Console.WriteLine("Continuing - invalid stop");
										wait.WaitOne();
									}
									switch (command) {
										case "run":
											wait = _mdbgProcess.Go();
											break;
										case "step_into":
											wait = _mdbgProcess.StepInto(false);
											break;
										case "step_over":
											wait = _mdbgProcess.StepOver(false);
											break;
										case "step_out":
											wait = _mdbgProcess.StepOut();
											break;
									}
									wait.WaitOne();
								//}
								outputMessage = this.ContinuationXml(parsedMessage.Item1, transId);
								break;
							case "stop":
								_mdbgProcess.Kill().WaitOne();
								outputMessage = this.ContinuationXml(parsedMessage.Item1, transId);
								return;
							case "stack_get": {
									var depth = int.Parse(getParamOrDefault("c", "0"));
									outputMessage = this.StackGetXml(transId, depth);
								}
								break;
							case "breakpoint_set":
								var type = getParamOrDefault("t", "");
								var file = getParamOrDefault("f", "");
								var line = int.Parse(getParamOrDefault("n", "0"));
								var state = getParamOrDefault("s", "");
								outputMessage = this.BreakpointSetXml(transId, type, file, line, state);
								break;
							case "breakpoint_remove":
								var id = int.Parse(getParamOrDefault("d", "0"));
								outputMessage = this.BreakpointRemoveXml(transId, id);
								break;
							default:
								outputMessage = this.ErrorXml(parsedMessage.Item1, transId, 4, "Test");
								break;
						}

						var realMessage = this.GenerateOutputMessage(outputMessage);
						_socket.Send(Encoding.UTF8.GetBytes(realMessage));
					}
				}
			} finally {
				if (_mdbgProcess.IsAlive) {
					_mdbgProcess.Breakpoints.DeleteAll();
					_mdbgProcess.Detach().WaitOne();
				}
				_socket.Close();
			}
		}

		private String GenerateOutputMessage(String message) {
			var length = message.Length;
			var result = String.Format("{0}\0{1}\0", length.ToString(), message);
			Console.WriteLine(result);
			return result;
		}

		private String ErrorXml(String command, String transId, int errorCode, String errorMessage) {
			return String.Format(
				"<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
				+"<response xmlns=\"urn:debugger_protocol_v1\" command=\"{0}\" transaction_id=\"{1}\">"
				+"	<error code=\"{2}\" apperr=\"{3}\">"
				+"		<message>{4}</message>"
				+"	</error>"
				+"</response>"
				,
				command, transId, errorCode, String.Empty, errorMessage);
		}

		private String InitXml(String path) {
			return String.Format(
				"<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
				+"<init xmlns=\"urn:debugger_protocol_v1\" appid=\"DotNetDbgp\" idekey=\"\" session=\"\" thread=\"\" parent=\"\" language=\"C#\" protocol_version=\"1.0\" fileuri=\"{0}\" />",
				path ?? "dbgp:null"
			);
		}

		private String ContextNamesXml(String transId) {
			return String.Format(
				"<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
				+"<response xmlns=\"urn:debugger_protocol_v1\" command=\"context_names\" transaction_id=\"{0}\">"
				+"	<context name=\"Both\" id=\"0\"/>"
				+"	<context name=\"Local\" id=\"1\"/>"
				+"	<context name=\"Arguments\" id=\"2\"/>"
				+"</response>",
				transId
			);
		}

		private String ContinuationXml(String command, String transId) {
			return String.Format(
				 "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
				+"<response xmlns=\"urn:debugger_protocol_v1\" command=\"{0}\" status=\"{2}\" reason=\"ok\" transaction_id=\"{1}\"/>",
				command, transId, _mdbgProcess.IsRunning ? "running" : "break"
			);
		}

		private String StackGetXml(String transId, int depth) {
			var activeThread = _mdbgProcess.Threads.HaveActive ? _mdbgProcess.Threads.Active : null;

			var sourcePosition = activeThread == null ? null : activeThread.Frames.Cast<MDbgFrame>().ElementAt(depth).SourcePosition;

			if (sourcePosition == null && activeThread != null && activeThread.CurrentSourcePosition != null) {
				sourcePosition = activeThread.CurrentSourcePosition;
			}
			
			return String.Format(
				 "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
				+"<response xmlns=\"urn:debugger_protocol_v1\" command=\"stack_get\" transaction_id=\"{0}\">"
				+"	<stack level=\"{3}\" type=\"file\" filename=\"{1}\" lineno=\"{2}\" where=\"\" cmdbegin=\"\" cmdend=\"\"/>"
				+"</response>",
				transId,
				(sourcePosition == null ? null : sourcePosition.Path) ?? "dbgp:null",
				(sourcePosition == null ? null : sourcePosition.Line.ToString()) ?? "",
				depth
			);
		}

		private String BreakpointSetXml(String transId, String type, String file, int line, String state) {
			if (state == "enabled" && type == "line") {
				if (file.StartsWith("file://")) {
					file = file.Substring(7);
				}
				file = file.Replace('/', '\\');
				var breakpoint = _mdbgProcess.Breakpoints.CreateBreakpoint(new BreakpointLineNumberLocation(file, line));
				//foreach(var appDom in _mdbgProcess.AppDomains.Cast<MDbgAppDomain>()) {
				//	Console.WriteLine("AppDomain: "+appDom.Number);
				//	foreach(var assembly in appDom.CorAppDomain.Assemblies.Cast<Microsoft.Samples.Debugging.CorDebug.CorAssembly>()) {
				//		Console.WriteLine("Assembly: "+assembly.Name);
				//	}
				//}
				//foreach (var module in _mdbgProcess.Modules.Cast<MDbgModule>()) {
				//	Console.WriteLine(module.Number);
				//}
				Console.WriteLine(String.Format("\nFile: {0}, Line: {1}, Breakpoint: {2}\n", file, line, breakpoint.ToString()));
				return String.Format(
					 "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
					+"<response xmlns=\"urn:debugger_protocol_v1\" command=\"breakpoint_set\" transaction_id=\"{0}\" state=\"{1}\" id=\"{2}\"/>",
					transId, state, breakpoint.Number
				);
			} else {
				throw new NotImplementedException(state+"-"+type);
			}
		}

		private String BreakpointRemoveXml(String transId, int id) {
				var breakpoint = _mdbgProcess.Breakpoints.Cast<MDbgBreakpoint>().FirstOrDefault(i => i.Number == id);
				if (breakpoint != null) {
					breakpoint.Delete();
				}
				//var breakpoint = _mdbgProcess.Breakpoints.CreateBreakpoint(new BreakpointLineNumberLocation(file, line));
				//foreach(var appDom in _mdbgProcess.AppDomains.Cast<MDbgAppDomain>()) {
				//	Console.WriteLine("AppDomain: "+appDom.Number);
				//	foreach(var assembly in appDom.CorAppDomain.Assemblies.Cast<Microsoft.Samples.Debugging.CorDebug.CorAssembly>()) {
				//		Console.WriteLine("Assembly: "+assembly.Name);
				//	}
				//}
				//foreach (var module in _mdbgProcess.Modules.Cast<MDbgModule>()) {
				//	Console.WriteLine(module.Number);
				//}
				//Console.WriteLine(String.Format("File: {0}, Line: {1}, Breakpoint: {2}", file, line, breakpoint.ToString()));
				return String.Format(
					 "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
					+"<response xmlns=\"urn:debugger_protocol_v1\" command=\"breakpoint_remove\" transaction_id=\"{0}\" />",
					transId
				);
		}

		private String ContextGetXml(String transId, int contextId, int depth) {
			var frame = depth == 0 ? _mdbgProcess.Threads.Active.CurrentFrame : _mdbgProcess.Threads.Active.Frames.Cast<MDbgFrame>().ElementAt(depth);

			var variables = new List<MDbgValue>();
			if (contextId == 0 || contextId == 1) {
				variables.AddRange(frame.Function.GetActiveLocalVars(frame));
			}
			if (contextId == 0 || contextId == 2) {
				variables.AddRange(frame.Function.GetArguments(frame));
			}

			var variablesString = new StringBuilder();
			foreach(var var in variables) {
				variablesString.Append(this.ContextGetPropertyXml(var, 3));
			}
			return String.Format(
				 "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
				+"<response xmlns=\"urn:debugger_protocol_v1\" command=\"context_get\" context=\"{1}\" transaction_id=\"{0}\">"
				+"{2}"
				+"</response>",
				transId,
				contextId,
				variablesString.ToString()
			);
		}


		private String PropertyGetXml(string transId, int contextId, string name, int depth) {
			var frame = depth == 0 ? _mdbgProcess.Threads.Active.CurrentFrame : _mdbgProcess.Threads.Active.Frames.Cast<MDbgFrame>().ElementAt(depth);

			var var = _mdbgProcess.ResolveVariable(name, frame);

			var variablesString = this.ContextGetPropertyXml(var, 3);

			return String.Format(
				 "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
				+"<response xmlns=\"urn:debugger_protocol_v1\" command=\"context_get\" context=\"{1}\" transaction_id=\"{0}\">"
				+"{2}"
				+"</response>",
				transId,
				contextId,
				variablesString.ToString()
			);
		}

		private String ContextGetPropertyXml(MDbgValue var, int depth) {
			var childProperties = new List<MDbgValue>();
			if (var.IsArrayType) {
				childProperties.AddRange(var.GetArrayItems());
			}
			if (var.IsComplexType) {
				childProperties.AddRange(var.GetFields());
			}
			var childPropertiesString = new StringBuilder();
			if (depth > 0) {
				foreach(var child in childProperties) {
					childPropertiesString.Append(this.ContextGetPropertyXml(child, depth-1));
				}
			}
			Func<String,String> e = (String i) => this.EscapeXml(i);
			var myValue = e(var.GetStringValue(0, false));
			return String.Format(
				"<property name=\"{0}\" fullname=\"{0}\" type=\"{1}\" classname=\"\" constant=\"0\" children=\"{2}\" size=\"{3}\" encoding=\"none\" numchildren=\"{2}\">{4}{5}</property>",
				e(var.Name), e(var.TypeName), childProperties.Count, myValue.Length+childPropertiesString.Length, myValue, childPropertiesString.ToString()
			);
		}

		private Tuple<String,IDictionary<String,String>,String> ParseInputMessage(String message) {
			var resultArguments = new Dictionary<String,String>();
			var commandSplitter = message.IndexOf(" ");
			var command = message.Substring(0, commandSplitter);
			//Console.WriteLine("Command: "+command);

			var inQuotes = false;
			var escape = false;
			var part = String.Empty;
			var parts = new List<String>();
			var i = commandSplitter + 1;
			for(; i < message.Length; i++) {
				var messageChar = message[i];
				if (!inQuotes) {
					if (messageChar == ' ') {
						if (part.Length != 0) {
							if (part == "--") {
								i++;
								break;
							}
							parts.Add(part);
							//Console.WriteLine("Part: "+part);
							part = String.Empty;
						}
						continue;
					} else if (messageChar == '"') {
						inQuotes = true;
						continue;
					}
				} else if (escape) {
					escape = false;
				} else if (messageChar == '"') {
					inQuotes = false;
					continue;
				} else if (messageChar == '\\') {
					escape = true;
					continue;
				}
				part += messageChar;
				//Console.WriteLine("Part: "+part);
			}

			if (part.Length != 0 && part != "--") {
				parts.Add(part);
				//Console.WriteLine("Part: "+part);
			}

			var body = message.Substring(i);
			//Console.WriteLine("Body: "+body);

			for(var j = 0; j + 1 < parts.Count; j += 2) {
				var key = parts[j];
				var val = parts[j+1];
				resultArguments[key] = val;
			}

			return Tuple.Create(command, (IDictionary<String,String>)resultArguments, body);
		}

		private String EscapeXml(String input) {
			return new System.Xml.Linq.XText(input).ToString();
		}

		public void Stop() {
			if (_mdbgProcess.IsAlive) {
				_mdbgProcess.Breakpoints.DeleteAll();
				_mdbgProcess.Detach().WaitOne();
			}
			_socket.Close();
		}
	}
}
