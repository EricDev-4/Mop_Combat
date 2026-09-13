import asyncio
import json
from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client


async def main():
    server = StdioServerParameters(
        command='C:/Users/jelle/AppData/Local/Unity/bin/unity.exe',
        args=['mcp', '--project-path', 'C:/UnityProject/Mop_Combat'],
        cwd='C:/UnityProject/Mop_Combat')
    async with stdio_client(server) as (reader, writer):
        async with ClientSession(reader, writer) as session:
            initialized = await session.initialize()
            catalog = await session.list_tools()
            print(json.dumps({'server': initialized.serverInfo.model_dump(),
                              'tool_count': len(catalog.tools),
                              'status_tools': [t.model_dump() for t in catalog.tools if 'status' in t.name]}, indent=2), flush=True)
            status = next((t for t in catalog.tools if t.name == 'editor_status'), None)
            if status and not status.inputSchema.get('required'):
                result = await session.call_tool(status.name, {})
                print(json.dumps(result.model_dump(mode='json'), indent=2), flush=True)
                if result.isError:
                    raise RuntimeError('MCP editor_status failed')


asyncio.run(main())
