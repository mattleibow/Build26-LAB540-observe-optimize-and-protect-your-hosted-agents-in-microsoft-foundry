# Zava Travel Concierge — Hosted Agent

A multi-agent orchestration system built with the [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) for .NET and hosted using the **Responses protocol**. The Concierge delegates to three specialist agents (flights, hotels, car rentals) using locally-defined C# tools registered with `AIFunctionFactory.Create`.

## How It Works

### Model Integration

The agent uses `AIProjectClient.AsAIAgent` from the Agent Framework to create a Responses client from the project endpoint and model deployment. The agent supports both streaming (SSE events) and non-streaming (JSON) response modes.

See [Program.cs](Program.cs) for the full implementation.

### Agent Hosting

The agent is hosted using the [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) with `AgentHost` and `MapFoundryResponses` (from the `Microsoft.Agents.AI.Foundry.Hosting` package), which provisions a REST API endpoint compatible with the OpenAI Responses protocol.

## Running the Agent Host

Follow the instructions in the [Running the Agent Host Locally](../../README.md#running-the-agent-host-locally) section of the README in the parent directory to run the agent host.

## Interacting with the agent

> Depending on how you run the agent host, you can invoke the agent using `curl` (`Invoke-WebRequest` in PowerShell) or `azd`. Please refer to the [parent README](../../README.md) for more details. Use this README for sample queries you can send to the agent.

Send a POST request to the server with a JSON body containing an "input" field to interact with the agent. For example:

```bash
curl -X POST http://localhost:8088/responses -H "Content-Type: application/json" -d '{"input": "What flights are available from Chicago to Rome?"}'
```

## Deploying the Agent to Foundry

To host the agent on Foundry, follow the instructions in the [Deploying the Agent to Foundry](../../README.md#deploying-the-agent-to-foundry) section of the README in the parent directory.
