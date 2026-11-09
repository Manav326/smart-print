#!/usr/bin/env python3
"""Apply Infinix TV Remote customizations to the pinned MIT Android source."""
from pathlib import Path
import re
import sys

root = Path(sys.argv[1]).resolve()
android = root / "android" if (root / "android").is_dir() else root

def edit(rel, replacements):
    path = android / rel
    text = path.read_text(encoding="utf-8")
    original = text
    for old, new, label in replacements:
        if new in text:
            continue
        count = text.count(old)
        if count != 1:
            raise SystemExit(f"{rel}: expected one occurrence for {label}, found {count}")
        text = text.replace(old, new, 1)
    if text != original:
        path.write_text(text, encoding="utf-8", newline="\n")

edit("app/src/main/java/com/luma/remote/protocol/AndroidTvProvider.kt", [
    ("class AndroidTvProvider(private val vault: Vault) : RemoteProvider {",
     """class AndroidTvProvider(
    private val vault: Vault,
    private val pairingPort: Int = 6467,
    private val remotePort: Int = 6466,
) : RemoteProvider {
    init {
        require(AndroidTvPortValidator.valid(pairingPort, remotePort)) { "Enter two different valid TCP ports." }
    }""", "inject configurable Android TV ports"),
    ("val s=tls(tv,6467,true)", "val s=tls(tv,pairingPort,true)", "use configurable pairing port"),
    ("val s=tls(tv,6466,false)", "val s=tls(tv,remotePort,false)", "use configurable remote port"),
    ('Wire.string(2,"OpenTV-Remote")', 'Wire.string(2,"Infinix TV Remote")', "brand pairing identity"),
])

edit("app/src/main/java/com/luma/remote/protocol/ManualTvDetection.kt", [
    ("internal object ManualTvDetection {",
     """internal object AndroidTvPortValidator {
    fun valid(pairingPort: Int, remotePort: Int): Boolean =
        pairingPort in 1..65535 && remotePort in 1..65535 && pairingPort != remotePort
}

internal object ManualTvDetection {""", "add Android TV port validation"),
    ("        samsung:suspend ()->SamsungInfo?\n    ):TvDevice?=coroutineScope {\n        require(LocalAddress.valid(host))",
     "        samsung:suspend ()->SamsungInfo?,\n        androidPairingPort:Int=6467,\n        androidRemotePort:Int=6466\n    ):TvDevice?=coroutineScope {\n        require(LocalAddress.valid(host))\n        require(AndroidTvPortValidator.valid(androidPairingPort, androidRemotePort))",
     "accept configurable Android TV ports"),
    ("val android=async { open(6467) }", "val android=async { open(androidPairingPort) && open(androidRemotePort) }", "probe configured Android TV ports"),
])

edit("app/src/main/java/com/luma/remote/RemoteViewModel.kt", [
    ('    private val ui=app.getSharedPreferences("ui",0)\n',
     """    private val ui=app.getSharedPreferences("ui",0)
    val connectionHost = MutableStateFlow(ui.getString("infinix-tv-host","192.168.31.216") ?: "192.168.31.216")
    val androidTvPairingPort = MutableStateFlow(ui.getInt("android-tv-pairing-port",6467).coerceIn(1,65535))
    val androidTvRemotePort = MutableStateFlow(ui.getInt("android-tv-remote-port",6466).coerceIn(1,65535))
""", "persist editable endpoint defaults"),
    ("    fun connect(candidate:TvDevice) {",
     "    fun connect(candidate:TvDevice, pairingPort:Int=androidTvPairingPort.value, remotePort:Int=androidTvRemotePort.value) {",
     "allow per-connection port overrides"),
    ("Platform.ANDROID_TV -> AndroidTvProvider(vault)",
     "Platform.ANDROID_TV -> AndroidTvProvider(vault,pairingPort,remotePort)",
     "pass configured ports into Android TV protocol"),
])

vm_path = android / "app/src/main/java/com/luma/remote/RemoteViewModel.kt"
vm_text = vm_path.read_text(encoding="utf-8")
new_manual = """    fun manual(host:String, pairingPort:Int=androidTvPairingPort.value, remotePort:Int=androidTvRemotePort.value) {
        val address=host.trim()
        if(!LocalAddress.valid(address)) { notice.value=strings.getString(R.string.status_enter_a_local_ipv4_address_such_as_192_168_1_20); return }
        if(!AndroidTvPortValidator.valid(pairingPort,remotePort)) { notice.value="Enter two different TCP ports between 1 and 65535."; return }
        connectionHost.value=address
        androidTvPairingPort.value=pairingPort
        androidTvRemotePort.value=remotePort
        ui.edit().putString("infinix-tv-host",address).putInt("android-tv-pairing-port",pairingPort).putInt("android-tv-remote-port",remotePort).apply()
        viewModelScope.launch {
            notice.value=strings.getString(R.string.status_checking_device_services)
            val detected=withContext(Dispatchers.IO) {
                fun open(port:Int)=runCatching { Socket().use { it.connect(InetSocketAddress(address,port),1500) };true }.getOrDefault(false)
                ManualTvDetection.detect(address,open=::open,samsung={ SamsungProbe.legacyInfo(address) },androidPairingPort=pairingPort,androidRemotePort=remotePort)
            }
            if(detected==null)notice.value=strings.getString(R.string.status_no_supported_remote_service_responded_turn_on_the_tv_and_check_ne)
            else {
                discovery.devices.update { old -> old.filterNot { it.id==detected.id }+detected }
                if(detected.platform==Platform.LG)notice.value=strings.getString(R.string.status_lg_uses_unencrypted_local_port_3000_connect_from_devices_only_on)
                else connect(detected,pairingPort,remotePort)
            }
        }
    }
"""
pattern = re.compile(r"    fun manual\(host:String\) \{.*?\n    \}\n(?=    fun bonded\(\))", re.S)
vm_new, n = pattern.subn(new_manual, vm_text)
if n != 1:
    raise SystemExit(f"RemoteViewModel.kt: expected one manual(host) function, found {n}")
if vm_new != vm_text:
    vm_path.write_text(vm_new, encoding="utf-8", newline="\n")

ui_path = android / "app/src/main/java/com/luma/remote/ui/OpenTVApp.kt"
ui_text = ui_path.read_text(encoding="utf-8")
ui_text = ui_text.replace('Text("OpenTV-Remote",Modifier.padding(start=8.dp)', 'Text("Infinix TV Remote",Modifier.padding(start=8.dp)', 1)
old_manual_dialog = """    if(manual) { var host by rememberSaveable() { mutableStateOf("") }; AlertDialog(containerColor=Panel,onDismissRequest={manual=false},title={Text(strings.getString(R.string.ui_connect_by_ip))},text={Column(verticalArrangement=Arrangement.spacedBy(16.dp)) { Text(strings.getString(R.string.ui_find_the_tv_s_local_ipv4_address_in_its_network_settings_we_ll_ch),color=Muted); OutlinedTextField(host,{host=it},label={Text(strings.getString(R.string.ui_tv_ip_address))},placeholder={Text("192.168.1.20")},singleLine=true,modifier=Modifier.fillMaxWidth()) }},confirmButton={TextButton(onClick={vm.manual(host);manual=false},enabled=host.isNotBlank()) { Text(strings.getString(R.string.ui_check_device)) }},dismissButton={TextButton(onClick={manual=false}) { Text(strings.getString(R.string.ui_cancel)) }}) }"""
new_manual_dialog = """    if(manual) {
        val savedHost by vm.connectionHost.collectAsStateWithLifecycle()
        val savedPairingPort by vm.androidTvPairingPort.collectAsStateWithLifecycle()
        val savedRemotePort by vm.androidTvRemotePort.collectAsStateWithLifecycle()
        var host by rememberSaveable { mutableStateOf(savedHost) }
        var pairingPortText by rememberSaveable { mutableStateOf(savedPairingPort.toString()) }
        var remotePortText by rememberSaveable { mutableStateOf(savedRemotePort.toString()) }
        val pairingPort=pairingPortText.toIntOrNull()
        val remotePort=remotePortText.toIntOrNull()
        val validPorts=pairingPort!=null && remotePort!=null && AndroidTvPortValidator.valid(pairingPort,remotePort)
        AlertDialog(
            containerColor=Panel,
            onDismissRequest={manual=false},
            title={Text(strings.getString(R.string.ui_connect_by_ip))},
            text={
                Column(Modifier.verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(12.dp)) {
                    Text(strings.getString(R.string.ui_find_the_tv_s_local_ipv4_address_in_its_network_settings_we_ll_ch),color=Muted)
                    OutlinedTextField(host,{host=it},label={Text(strings.getString(R.string.ui_tv_ip_address))},placeholder={Text("192.168.31.216")},singleLine=true,modifier=Modifier.fillMaxWidth())
                    OutlinedTextField(pairingPortText,{pairingPortText=it.filter { c -> c in '0'..'9' }.take(5)},label={Text(strings.getString(R.string.ui_pairing_port))},keyboardOptions=KeyboardOptions(keyboardType=androidx.compose.ui.text.input.KeyboardType.Number),singleLine=true,modifier=Modifier.fillMaxWidth())
                    OutlinedTextField(remotePortText,{remotePortText=it.filter { c -> c in '0'..'9' }.take(5)},label={Text(strings.getString(R.string.ui_remote_control_port))},keyboardOptions=KeyboardOptions(keyboardType=androidx.compose.ui.text.input.KeyboardType.Number),singleLine=true,modifier=Modifier.fillMaxWidth())
                    Text(strings.getString(R.string.ui_android_tv_remote_ports_hint),color=Muted,fontSize=12.sp)
                }
            },
            confirmButton={TextButton(onClick={vm.manual(host,pairingPort!!,remotePort!!);manual=false},enabled=LocalAddress.valid(host.trim()) && validPorts) { Text(strings.getString(R.string.ui_check_device)) }},
            dismissButton={TextButton(onClick={manual=false}) { Text(strings.getString(R.string.ui_cancel)) }}
        )
    }"""
if 'Text("Infinix TV Remote",Modifier.padding(start=8.dp)' not in ui_text:
    raise SystemExit("OpenTVApp.kt: could not brand title")
if new_manual_dialog not in ui_text:
    count=ui_text.count(old_manual_dialog)
    if count != 1:
        raise SystemExit(f"OpenTVApp.kt: expected one manual IP dialog, found {count}")
    ui_text=ui_text.replace(old_manual_dialog,new_manual_dialog,1)
ui_path.write_text(ui_text,encoding="utf-8",newline="\n")

manifest = android / "app/src/main/AndroidManifest.xml"
m = manifest.read_text(encoding="utf-8")
if 'android:label="Infinix TV Remote"' not in m:
    if m.count('android:label="OpenTV-Remote"') != 1:
        raise SystemExit("AndroidManifest.xml: expected one OpenTV-Remote app label")
    m=m.replace('android:label="OpenTV-Remote"','android:label="Infinix TV Remote"',1)
    manifest.write_text(m,encoding="utf-8",newline="\n")

strings = android / "app/src/main/res/values/strings.xml"
s = strings.read_text(encoding="utf-8")
if '<string name="ui_pairing_port">' not in s:
    s=s.replace("</resources>", """    <string name="ui_pairing_port">Pairing port</string>
    <string name="ui_remote_control_port">Remote-control port</string>
    <string name="ui_android_tv_remote_ports_hint">Android TV Remote v2 defaults: pairing 6467, remote control 6466. Edit these only when your TV uses different ports.</string>
</resources>""")
    strings.write_text(s,encoding="utf-8",newline="\n")

ru = android / "app/src/main/res/values-ru/strings.xml"
if ru.exists():
    s=ru.read_text(encoding="utf-8")
    if '<string name="ui_pairing_port">' not in s:
        s=s.replace("</resources>", """    <string name="ui_pairing_port">Порт сопряжения</string>
    <string name="ui_remote_control_port">Порт управления</string>
    <string name="ui_android_tv_remote_ports_hint">Порты Android TV Remote v2 по умолчанию: сопряжение 6467, управление 6466. Меняйте их, только если телевизор использует другие порты.</string>
</resources>""")
        ru.write_text(s,encoding="utf-8",newline="\n")

test = android / "app/src/test/java/com/luma/remote/protocol/AndroidTvPortValidatorTest.kt"
test.parent.mkdir(parents=True,exist_ok=True)
test.write_text("""package com.luma.remote.protocol

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AndroidTvPortValidatorTest {
    @Test fun acceptsStandardAndroidTvRemoteV2Ports() {
        assertTrue(AndroidTvPortValidator.valid(6467,6466))
    }
    @Test fun acceptsDistinctCustomPorts() {
        assertTrue(AndroidTvPortValidator.valid(16467,16466))
    }
    @Test fun rejectsInvalidOrIdenticalPorts() {
        assertFalse(AndroidTvPortValidator.valid(0,6466))
        assertFalse(AndroidTvPortValidator.valid(6467,65536))
        assertFalse(AndroidTvPortValidator.valid(6466,6466))
    }
}
""",encoding="utf-8",newline="\n")

print("Applied Infinix branding, editable host/ports, protocol port injection and validator tests.")
