import asyncio
import base64
import json
from pathlib import Path

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

ROOT = Path('C:/Reddy/Assets/Pickaxe_32x32_v1')
SETTINGS = Path('C:/Users/jelle/.agents/tools/aseprite-animation/settings.json')


async def main():
    if ROOT.exists():
        raise RuntimeError(f'Refusing to overwrite existing test folder: {ROOT}')
    settings = json.loads(SETTINGS.read_text(encoding='utf-8-sig'))
    ROOT.mkdir(parents=True)
    stages = ROOT / 'work'
    stages.mkdir()
    parameters = StdioServerParameters(command=settings['python'], args=[
        '-X', 'utf8', str(SETTINGS.parent / 'runtime/server.py'), '--config', str(SETTINGS)])
    receipts = []
    async with stdio_client(parameters) as (reader, writer):
        async with ClientSession(reader, writer) as session:
            await session.initialize()

            async def call(name, arguments):
                result = await session.call_tool(name, arguments)
                data = result.model_dump(mode='json')
                for block in data.get('content', []):
                    if block.get('type') == 'image':
                        preview = ROOT / 'pickaxe_preview_8x.png'
                        preview.write_bytes(base64.b64decode(block.pop('data')))
                        block['saved_path'] = str(preview)
                receipts.append({'tool': name, 'arguments': arguments, 'result': data})
                (ROOT / 'mcp_receipts.json').write_text(json.dumps(receipts, indent=2), encoding='utf-8')
                if result.isError:
                    raise RuntimeError(json.dumps(data))
                print(name + ': OK', flush=True)
                return data

            print(json.dumps(await call('aseprite_bridge_status', {})), flush=True)
            base = str(stages / '01_blank.aseprite')
            wood = str(stages / '02_handle.aseprite')
            final = str(ROOT / 'pickaxe.aseprite')
            await call('aseprite_sprite_create', {'output': base, 'width': 32, 'height': 32, 'color_mode': 'rgb', 'layer_name': 'Wood handle'})
            # Diagonal handle with a one-pixel dark contour and warm highlight.
            pixels = {}
            for y in range(11, 28):
                center = 33 - y
                for dx in range(-2, 3):
                    pixels[(center + dx, y)] = '#292735'
                for dx, color in [(-1, '#D4A05D'), (0, '#A86E3F'), (1, '#754730')]:
                    pixels[(center + dx, y)] = color
            for x in range(5, 9):
                pixels[(x, 28)] = '#292735'
            await call('aseprite_sprite_edit', {'path': base, 'output': wood, 'layer': 'Wood handle', 'operations': [
                {'type': 'pixels', 'pixels': [{'x': x, 'y': y, 'color': c} for (x, y), c in pixels.items()]}]})
            # Curved, pointed steel head; authored at native pixel coordinates.
            rows = {
                4: (8, 'OOOOOO'),
                5: (5, 'OOHHHHHOOO'),
                6: (3, 'OOHHHHHMMMMOO'),
                7: (4, 'OOOMMMMMMMMMOO'),
                8: (7, 'OOOSSSMMMMMMMOO'),
                9: (10, 'OOOSSSMMMMMMOO'),
                10: (13, 'OOSSSMMMMMMOO'),
                11: (16, 'OOSSSMMMMMOO'),
                12: (18, 'OOSSSMMMMOO'),
                13: (20, 'OOSSSMMMOO'),
                14: (22, 'OOSSMMOO'),
                15: (24, 'OOSMMO'),
                16: (25, 'OOSMO'),
                17: (26, 'OSO'),
                18: (27, 'OO'),
                19: (28, 'O'),
            }
            palette = {'O': '#292735', 'H': '#E1EEF0', 'M': '#9EBDC7', 'S': '#587786'}
            head = [{'x': start + i, 'y': y, 'color': palette[ch]} for y, (start, row) in rows.items() for i, ch in enumerate(row)]
            await call('aseprite_sprite_edit', {'path': wood, 'output': final, 'layer': 'Steel head', 'create_layer': True,
                'operations': [{'type': 'pixels', 'pixels': head}]})
            print(json.dumps(await call('aseprite_sprite_inspect', {'path': final})), flush=True)
            await call('aseprite_export', {'path': final, 'output': str(ROOT / 'pickaxe.png')})
            await call('aseprite_preview', {'path': final, 'frame': 1, 'scale': 8})
            await call('aseprite_pixels_inspect', {'path': final, 'x': 0, 'y': 0, 'width': 32, 'height': 32})
            print('Saved: ' + str(ROOT), flush=True)


asyncio.run(main())
