import json,subprocess,shutil,sys
code=sys.stdin.buffer.read().decode('utf-8-sig')
p=subprocess.run([shutil.which('unity'),'command','eval',code,'--runtime','MopCombat','--format','json'],capture_output=True,text=True,encoding='utf-8')
try:
 result=json.loads(p.stdout)
 print(json.dumps(result.get('data',{}).get('result',result) if result.get('data') else result,ensure_ascii=True))
except Exception: print(p.stdout,p.stderr)

