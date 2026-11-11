"""Windows integration tests; only synthetic data, new outputs, no customer inputs.

Run Build.ps1 -IncludeTestTools first, then python -B tests/test_portability.py.
Test outputs are deliberately retained under outputs/portability-*.
"""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import unittest
import uuid
import zipfile
import xml.etree.ElementTree as ET

REPO = Path(__file__).resolve().parents[1]
ENGINE = Path('work/segmented-20261109')
sys.dont_write_bytecode = True


@unittest.skipUnless(os.name == 'nt', 'Windows Job Object tests')
class Portability(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.previous_configuration={key:os.environ.pop(key,None) for key in ('CADTEST_ROOT','CADTEST_NODE')}
        cls.evidence = REPO/'outputs'/('portability-'+uuid.uuid4().hex[:12])
        cls.root = cls.evidence/'移位 workspace'
        cls.root.mkdir(parents=True)
        cls.env = os.environ.copy()
        cls.env.pop('CADTEST_ROOT', None)
        cls.env.pop('CADTEST_NODE', None)
        cls.env['PYTHONUTF8'] = '1'
        for name in ('Run.ps1', 'global.json', 'NuGet.Config'):
            shutil.copy2(REPO/name, cls.root/name)
        shutil.copytree(REPO/ENGINE, cls.root/ENGINE,
                        ignore=shutil.ignore_patterns('obj', '__pycache__'))
        sys.path.insert(0, str(cls.root/ENGINE))
        # Import an actual relocated runner, rather than patching ROOT in a test.
        spec = importlib.util.spec_from_file_location('relocated_runner', cls.root/ENGINE/'runner.py')
        cls.runner = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = cls.runner
        spec.loader.exec_module(cls.runner)
        assert cls.runner.ROOT == cls.root.resolve()
        cls.env['CADTEST_ROOT'] = str(cls.root.resolve())
        cls.limits = cls.runner.Limits(memory_mib=1024, cpu_percent=25, timeout_seconds=120)
        cls.counter = 0

    @classmethod
    def tearDownClass(cls):
        for key,value in cls.previous_configuration.items():
            if value is not None: os.environ[key]=value

    def guarded(self, args, expected=0):
        type(self).counter += 1
        folder=self.evidence/f'command-{self.counter:02d}'
        folder.mkdir()
        with self.runner.workspace_lock(self.root):
            result=self.runner.command(args,folder,'stage',self.limits,wait_for_go=True,cwd=self.root)
        (folder/'result.json').write_text(json.dumps(result,indent=2),encoding='utf8')
        self.assertEqual(result['returncode'],expected,(result,(folder/'stage.stderr.log').read_text(encoding='utf8',errors='replace')))
        self.assertTrue(result['guards_installed'])
        self.assertTrue(result['input_gate_released'])
        return folder

    def test_01_discovery_does_not_depend_on_current_directory(self):
        engine=self.root/ENGINE
        code="import workspace_paths;print(workspace_paths.workspace_root());print(workspace_paths.node_executable())"
        environment=self.env.copy()
        environment.pop('CADTEST_ROOT')
        environment['PYTHONPATH']=str(engine)
        run=subprocess.run([sys.executable,'-B','-c',code],cwd=self.evidence,env=environment,capture_output=True,text=True,encoding='utf8')
        self.assertEqual(run.returncode,0,run.stderr)
        self.assertEqual(run.stdout.splitlines()[0],str(self.root.resolve()))
        self.assertTrue(Path(run.stdout.splitlines()[1]).is_file())
        environment['CADTEST_ROOT']='relative-workspace'
        bad=subprocess.run([sys.executable,'-B','-c',code],cwd=self.evidence,env=environment,capture_output=True,text=True,encoding='utf8')
        self.assertNotEqual(bad.returncode,0)

    def test_02_path_containment_and_existing_outputs(self):
        for target in (self.evidence/'escape',self.root/'outputs',self.root.parent/(self.root.name+'-sibling')/'out'):
            with self.assertRaises(ValueError): self.runner.prepare_output(target,self.root,require_new=True)
        target=self.root/'outputs'/'unique'
        self.runner.prepare_output(target,self.root,require_new=True)
        marker=target/'keep.txt';marker.write_text('original',encoding='utf8')
        with self.assertRaises(ValueError): self.runner.prepare_output(target,self.root,require_new=True)
        self.assertEqual(marker.read_text(),'original')

    def test_03_shared_lock_still_excludes_second_process(self):
        code="import runner;\nwith runner.workspace_lock(): print('acquired')"
        environment=self.env.copy();environment['PYTHONPATH']=str(self.root/ENGINE)
        with self.runner.workspace_lock(self.root):
            busy=subprocess.run([sys.executable,'-B','-c',code],cwd=self.evidence,env=environment,capture_output=True,text=True,encoding='utf8')
        self.assertNotEqual(busy.returncode,0)
        self.assertIn('workspace_busy',busy.stderr)
        free=subprocess.run([sys.executable,'-B','-c',code],cwd=self.evidence,env=environment,capture_output=True,text=True,encoding='utf8')
        self.assertEqual(free.returncode,0,free.stderr)

    def test_04_frozen_python_stage_receives_workspace(self):
        folder=self.root/'outputs'/'freeze-check';folder.mkdir(parents=True)
        frozen,manifest=self.runner.freeze_sources(folder,self.root)
        helper=self.runner.APP_REL/'workspace_paths.py'
        self.assertIn(str(helper),manifest)
        probe=self.root/'probe.py'
        probe.write_text('import workspace_paths,json\nprint(json.dumps({"root":str(workspace_paths.workspace_root()),"helper":workspace_paths.__file__}))\n',encoding='utf8')
        # The real frozen gate sets sys.path to the executed script's directory.
        target=frozen/self.runner.APP_REL/'probe.py'
        target.write_bytes(probe.read_bytes())
        logs=self.guarded([sys.executable,'-B',frozen/self.runner.PYTHON_GATE,target])
        observed=json.loads((logs/'stage.stdout.log').read_text(encoding='utf8'))
        self.assertEqual(observed['root'],str(self.root.resolve()))
        self.assertEqual(Path(observed['helper']).resolve(),(frozen/helper).resolve())
        self.runner.verify_frozen(frozen,manifest)
        target_helper=frozen/helper
        target_helper.write_bytes(target_helper.read_bytes()+b'\n# tampered\n')
        with self.assertRaises(ValueError): self.runner.verify_frozen(frozen,manifest)

    def test_05_review_prepare_confirm_export_and_independent_xlsx_read(self):
        source=self.root/'inputs'/'synthetic.dwg';source.parent.mkdir(exist_ok=True)
        source.write_bytes(b'SYNTHETIC TEST ONLY - NOT A CAD FILE')
        digest=hashlib.sha256(source.read_bytes()).hexdigest().upper()
        data={'schema_version':1,'task_id':'SYNTHETIC-PORTABLE','title':'Synthetic only',
              'sources':[{'path':str(source),'sha256':digest}],
              'files':[{'id':'F1','name':source.name,'path':str(source),'sha256':digest,'diagnostics':'Synthetic test','requires_acknowledgement':True}],
              'items':[{'id':f'I{i}','file_id':'F1','cad':{'drawing_number':f'TEST-{i}','name':'TEST','material':'TEST','treatment':'TEST','quantity':i,'unit':'EA'},'warnings':'','requires_review':False,'kind':'frame','parents':[],'quantity_evidence':'synthetic','treatment_evidence':'synthetic','reference_info':''} for i in (1,2)]}
        task=self.root/'outputs'/'review-test';task.mkdir(parents=True)
        dataset=task/'dataset.json';dataset.write_text(json.dumps(data),encoding='utf8')
        program=self.root/ENGINE/'review-native/CadReviewWorkflow.exe'
        review=task/'review'
        self.guarded([program,'prepare','--input',dataset,'--output',review,'--wait-for-go'])
        original=review/'图纸复核.xlsx'
        self.guarded([program,'validate','--manifest',review/'source.json','--review',original,'--wait-for-go'],expected=2)
        confirmed=task/'synthetic-confirmed.xlsx'
        fixture=self.root/ENGINE/'fixture-tests/bin/Release/net10.0/FixtureTests.exe'
        # The test helper has no input gate; run it behind the existing Python gate.
        driver=self.root/'fixture_driver.py'
        driver.write_text('import subprocess,sys\nraise SystemExit(subprocess.call(sys.argv[1:]))\n',encoding='utf8')
        self.guarded([sys.executable,'-B',self.root/ENGINE/'python-gate.py',driver,fixture,'fixture',original,confirmed])
        exported=task/'export'
        self.guarded([program,'export','--manifest',review/'source.json','--review',confirmed,'--output',exported,'--reviewer','SYNTHETIC TEST ONLY','--wait-for-go'])
        xlsx=exported/'确认明细.xlsx'
        with zipfile.ZipFile(xlsx) as package:
            ns={'s':'http://schemas.openxmlformats.org/spreadsheetml/2006/main'}
            sheet=ET.fromstring(package.read('xl/worksheets/sheet1.xml'))
            values={cell.attrib['r']:cell for cell in sheet.findall('.//s:c',ns)}
            self.assertEqual(values['F8'].find('s:v',ns).text,'2')
            self.assertEqual(values['F9'].find('s:v',ns).text,'0')
            self.assertIsNotNone(values['I8'].find('s:f',ns))
            self.assertIsNotNone(values['I9'].find('s:f',ns))
            self.assertTrue('H8' not in values or values['H8'].find('s:v',ns) is None)
        self.guarded([sys.executable,'-B',self.root/ENGINE/'python-gate.py',driver,fixture,'recalc',xlsx])
        # The C# layer must independently reject outside and sibling-prefix paths.
        self.guarded([program,'prepare','--input',dataset,'--output',self.evidence/'outside-review','--wait-for-go'],expected=1)
        self.assertFalse((self.evidence/'outside-review').exists())
        source.write_bytes(b'TAMPERED SYNTHETIC INPUT')
        self.guarded([program,'validate','--manifest',review/'source.json','--review',confirmed,'--wait-for-go'],expected=2)

    def test_06_powershell_rejects_existing_task_before_processing(self):
        task=self.root/'outputs'/'ps-existing';task.mkdir(parents=True)
        sentinel=task/'sentinel';sentinel.write_text('keep')
        result=subprocess.run(['pwsh','-NoProfile','-File',str(self.root/'Run.ps1'),'-Mode','Recognize','-Drawing','inputs/no-file.dwg','-Output',str(task),'-Python',sys.executable],cwd=self.evidence,env=self.env,capture_output=True,text=True,encoding='utf8')
        self.assertNotEqual(result.returncode,0)
        self.assertEqual(sentinel.read_text(),'keep')
        self.assertFalse((task/'recognition').exists())

    def test_07_parallel_manifest_resolves_relative_paths_and_blocks_escape(self):
        import argparse
        import parallel_runner
        source=self.root/'inputs'/'manifest-test.dwg';source.parent.mkdir(exist_ok=True)
        source.write_bytes(b'SYNTHETIC INPUT')
        manifest=self.root/'relative-batch.json'
        data={'tasks':[{'id':'T1','drawing':'inputs/manifest-test.dwg','output':'outputs/relative-task'}]}
        manifest.write_text(json.dumps(data),encoding='utf8')
        args=argparse.Namespace(parallel_batch='relative-batch.json')
        tasks,_,_=parallel_runner.parse_manifest(args,self.root,self.root/'outputs/relative-batch',self.runner.Limits())
        self.assertEqual(tasks[0]['drawing'],str(source.resolve()))
        self.assertEqual(tasks[0]['output'],str((self.root/'outputs/relative-task').resolve()))
        data['tasks'][0]['output']='../escape'
        manifest.write_text(json.dumps(data),encoding='utf8')
        with self.assertRaises(ValueError):
            parallel_runner.parse_manifest(args,self.root,self.root/'outputs/relative-batch',self.runner.Limits())

    def test_08_cli_relative_input_reaches_reader_without_cwd_dependency(self):
        source=self.root/'inputs'/'invalid-native.dwg';source.parent.mkdir(exist_ok=True)
        source.write_bytes(b'AC1032 intentionally invalid synthetic DWG')
        result=subprocess.run([sys.executable,'-B',str(self.root/ENGINE/'runner.py'),'--drawing','inputs/invalid-native.dwg','--output','outputs/relative-native','--memory-mib','512'],cwd=self.evidence,env=self.env,capture_output=True,text=True,encoding='utf8',timeout=60)
        self.assertEqual(result.returncode,1,result.stdout+result.stderr)
        selection=json.loads((self.root/'outputs/relative-native/selection.json').read_text(encoding='utf8'))
        self.assertEqual(selection['selected'][0]['dwg'],str(source.resolve()))
        status=json.loads((self.root/'outputs/relative-native/samples/D001/run.json').read_text(encoding='utf8'))
        self.assertTrue(status['native']['guards_installed'])
        self.assertTrue(status['native']['input_gate_released'])
        self.assertEqual(status['status'],'failed')


if __name__=='__main__':
    unittest.main(verbosity=2)
