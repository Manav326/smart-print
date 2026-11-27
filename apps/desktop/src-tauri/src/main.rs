#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use serde::{Deserialize, Serialize};
use std::io::{BufRead, BufReader, Write};
use std::time::Duration;

const AGENT_STATUS_URL: &str = "http://127.0.0.1:17842/api/v1/local/status";
const AGENT_PIPE_NAME: &str = "QRtoPrint.SmartPrint.Agent.Control";

fn agent_pipe_path() -> String {
    format!(r"\\.\pipe\{AGENT_PIPE_NAME}")
}
const SESSION_TARGET: &str = "QRtoPrint.SmartPrint.Session";

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
struct AgentRuntimeReadiness {
    queue_initialized: bool,
    print_worker_running: bool,
    last_heartbeat_utc: Option<String>,
    heartbeat_fresh: bool,
    last_printer_inventory_utc: Option<String>,
    printer_inventory_fresh: bool,
    discovered_printer_count: usize,
    last_print_work_cycle_utc: Option<String>,
    last_failure_code: Option<String>,
    local_upload_ready: bool,
}

impl Default for AgentRuntimeReadiness {
    fn default() -> Self {
        Self {
            queue_initialized: false,
            print_worker_running: false,
            last_heartbeat_utc: None,
            heartbeat_fresh: false,
            last_printer_inventory_utc: None,
            printer_inventory_fresh: false,
            discovered_printer_count: 0,
            last_print_work_cycle_utc: None,
            last_failure_code: None,
            local_upload_ready: false,
        }
    }
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct LocalAgentStatus {
    service_reachable: bool,
    local_api_reachable: bool,
    paired: bool,
    agent_device_id: Option<String>,
    status: String,
    api_base_url: Option<String>,
    readiness: Option<AgentRuntimeReadiness>,
}

#[derive(Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct LocalNetworkAdapter {
    id: String,
    display_name: String,
    kind: String,
    ipv4_address: String,
    prefix_length: u8,
}

#[derive(Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct LocalNetworkSnapshot {
    networks: Vec<LocalNetworkAdapter>,
    selected_network_id: Option<String>,
    endpoints: Vec<String>,
    phone_verified_utc: Option<String>,
}

#[derive(Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct LocalPhoneTest {
    phone_test_url: String,
    started_utc: String,
    expires_utc: String,
}

#[derive(Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct AgentPipeResponse {
    success: bool,
    agent_device_id: Option<String>,
    message: Option<String>,
    service_reachable: Option<bool>,
    paired: Option<bool>,
    status: Option<String>,
    local_api_reachable: Option<bool>,
    api_base_url: Option<String>,
    code: Option<String>,
    network_snapshot: Option<LocalNetworkSnapshot>,
    phone_test: Option<LocalPhoneTest>,
    readiness: Option<AgentRuntimeReadiness>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct AgentPipeRequest<'a> {
    operation: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    enrollment_code: Option<&'a str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    network_id: Option<&'a str>,
}

#[tauri::command]
async fn get_local_agent_status() -> LocalAgentStatus {
    let client = match reqwest::Client::builder().timeout(Duration::from_secs(2)).build() {
        Ok(client) => client,
        Err(_) => return pipe_agent_status().unwrap_or_else(|_| unavailable_status()),
    };

    match client.get(AGENT_STATUS_URL).send().await {
        Ok(response) if response.status().as_u16() == 503 => {
            let problem = response.json::<serde_json::Value>().await.ok();
            let title = problem.as_ref()
                .and_then(|value| value.get("title"))
                .and_then(serde_json::Value::as_str);
            let api_base_url = problem.as_ref()
                .and_then(|value| value.get("apiBaseUrl"))
                .and_then(serde_json::Value::as_str)
                .map(str::to_owned);
            let readiness = problem.as_ref()
                .and_then(|value| value.get("readiness"))
                .and_then(|value| serde_json::from_value::<AgentRuntimeReadiness>(value.clone()).ok());
            if title == Some("This computer's SmartPrint pairing was revoked by its shop owner.") {
                LocalAgentStatus {
                    service_reachable: true,
                    local_api_reachable: true,
                    paired: false,
                    agent_device_id: None,
                    status: "REVOKED".into(),
                    api_base_url,
                    readiness: readiness.clone(),
                }
            } else if title == Some("This computer is not paired with a print shop.") {
                LocalAgentStatus {
                    service_reachable: true,
                    local_api_reachable: true,
                    paired: false,
                    agent_device_id: None,
                    status: "UNPAIRED".into(),
                    api_base_url,
                    readiness: readiness.clone(),
                }
            } else {
                pipe_agent_status().unwrap_or_else(|_| unavailable_status())
            }
        },
        Ok(response) if response.status().is_success() => {
            match response.json::<serde_json::Value>().await {
                Ok(payload) => {
                    let id = payload.get("agentDeviceId")
                        .and_then(serde_json::Value::as_str)
                        .map(str::to_owned);
                    let readiness = payload.get("readiness")
                        .and_then(|value| serde_json::from_value::<AgentRuntimeReadiness>(value.clone()).ok());
                    LocalAgentStatus {
                        service_reachable: true,
                        local_api_reachable: true,
                        paired: id.is_some(),
                        agent_device_id: id,
                        status: payload.get("status")
                            .and_then(serde_json::Value::as_str)
                            .unwrap_or("CONNECTED")
                            .to_owned(),
                        api_base_url: payload.get("apiBaseUrl")
                            .and_then(serde_json::Value::as_str)
                            .map(str::to_owned),
                        readiness,
                    }
                }
                Err(_) => pipe_agent_status().unwrap_or_else(|_| unavailable_status()),
            }
        }
        Ok(_) | Err(_) => pipe_agent_status().unwrap_or_else(|_| unavailable_status()),
    }
}

fn unavailable_status() -> LocalAgentStatus {
    LocalAgentStatus {
        service_reachable: false,
        local_api_reachable: false,
        paired: false,
        agent_device_id: None,
        status: "SERVICE_UNAVAILABLE".into(),
        api_base_url: None,
        readiness: None,
    }
}

#[cfg(windows)]
fn send_agent_pipe_request(request: &AgentPipeRequest<'_>) -> Result<AgentPipeResponse, String> {
    use std::fs::OpenOptions;

    let mut pipe = OpenOptions::new()
        .read(true)
        .write(true)
        .open(agent_pipe_path())
        .map_err(|_| "The SmartPrint background service is not ready. Please retry shortly.".to_string())?;

    let request = serde_json::to_vec(request)
        .map_err(|_| "Could not prepare the SmartPrint service request.".to_string())?;
    pipe.write_all(&request)
        .and_then(|_| pipe.write_all(b"\n"))
        .and_then(|_| pipe.flush())
        .map_err(|_| "Could not send the request to the SmartPrint background service.".to_string())?;

    let mut response_line = String::new();
    BufReader::new(pipe)
        .read_line(&mut response_line)
        .map_err(|_| "The SmartPrint background service did not return a response.".to_string())?;
    serde_json::from_str(&response_line)
        .map_err(|_| "The SmartPrint background service returned an invalid response.".to_string())
}

#[cfg(windows)]
fn pipe_agent_status() -> Result<LocalAgentStatus, String> {
    let response = send_agent_pipe_request(&AgentPipeRequest {
        operation: "status",
        enrollment_code: None,
        network_id: None,
    })?;
    if response.service_reachable != Some(true) {
        return Err(response.message.unwrap_or_else(|| "The SmartPrint background service is not responding.".into()));
    }

    let paired = response.paired.unwrap_or(false);
    Ok(LocalAgentStatus {
        service_reachable: true,
        local_api_reachable: response.local_api_reachable.unwrap_or(false),
        paired,
        agent_device_id: response.agent_device_id,
        status: response.status.unwrap_or_else(|| if paired { "CONNECTED" } else { "UNPAIRED" }.into()),
        api_base_url: response.api_base_url,
        readiness: response.readiness,
    })
}

#[cfg(not(windows))]
fn pipe_agent_status() -> Result<LocalAgentStatus, String> {
    Err("The local Windows Agent pipe is unavailable on this platform.".into())
}

#[tauri::command]
fn get_local_network_snapshot() -> Result<LocalNetworkSnapshot, String> {
    #[cfg(windows)]
    {
        let response = send_agent_pipe_request(&AgentPipeRequest {
            operation: "network-status",
            enrollment_code: None,
            network_id: None,
        })?;
        if !response.success {
            return Err(response.message.unwrap_or_else(|| "Could not read the shop connection.".into()));
        }
        return response.network_snapshot.ok_or_else(|| "The shop connection details were unavailable.".into());
    }
    #[cfg(not(windows))]
    { Err("Shop connection settings are available on Windows only.".into()) }
}

#[tauri::command]
fn select_local_network(interface_id: String) -> Result<LocalNetworkSnapshot, String> {
    if interface_id.is_empty() || interface_id.len() > 256 {
        return Err("Choose an available shop connection.".into());
    }
    #[cfg(windows)]
    {
        let response = send_agent_pipe_request(&AgentPipeRequest {
            operation: "select-network",
            enrollment_code: None,
            network_id: Some(&interface_id),
        })?;
        if !response.success {
            return Err(response.message.unwrap_or_else(|| "Could not select that shop connection.".into()));
        }
        return response.network_snapshot.ok_or_else(|| "The selected shop connection was unavailable.".into());
    }
    #[cfg(not(windows))]
    { let _ = interface_id; Err("Shop connection settings are available on Windows only.".into()) }
}

#[tauri::command]
fn start_local_phone_test() -> Result<LocalPhoneTest, String> {
    #[cfg(windows)]
    {
        let response = send_agent_pipe_request(&AgentPipeRequest {
            operation: "start-phone-test",
            enrollment_code: None,
            network_id: None,
        })?;
        if !response.success {
            return Err(response.message.unwrap_or_else(|| "Could not start the phone connection test.".into()));
        }
        return response.phone_test.ok_or_else(|| "The phone connection test could not be started.".into());
    }
    #[cfg(not(windows))]
    { Err("Phone connection tests are available on Windows only.".into()) }
}

#[tauri::command]
async fn enroll_local_agent(enrollment_code: String) -> Result<AgentPipeResponse, String> {
    if enrollment_code.len() != 32 ||
        !enrollment_code.bytes().all(|byte| byte.is_ascii_alphanumeric() || byte == b'-' || byte == b'_') {
        return Err("The service returned an invalid one-time enrollment authorization.".into());
    }

    #[cfg(windows)]
    {
        let response = send_agent_pipe_request(&AgentPipeRequest {
            operation: "enroll",
            enrollment_code: Some(&enrollment_code),
            network_id: None,
        })?;
        if !response.success && response.code.is_none() {
            return Err(response.message.clone().unwrap_or_else(|| "This computer could not be connected to the shop.".into()));
        }
        Ok(response)
    }

    #[cfg(not(windows))]
    {
        let _ = enrollment_code;
        Err("SmartPrint desktop enrollment is supported on Windows only.".into())
    }
}

#[tauri::command]
fn read_secure_session() -> Result<Option<String>, String> {
    #[cfg(windows)]
    { credential_store::read() }
    #[cfg(not(windows))]
    { Ok(None) }
}

#[tauri::command]
fn write_secure_session(session: Option<String>) -> Result<(), String> {
    #[cfg(windows)]
    { credential_store::write(session) }
    #[cfg(not(windows))]
    { let _ = session; Ok(()) }
}

#[cfg(windows)]
mod credential_store {
    use super::SESSION_TARGET;
    use std::ptr::null_mut;
    use windows_sys::Win32::Foundation::{GetLastError, ERROR_NOT_FOUND};
    use windows_sys::Win32::Security::Credentials::{
        CredDeleteW, CredFree, CredReadW, CredWriteW, CREDENTIALW,
        CRED_PERSIST_LOCAL_MACHINE, CRED_TYPE_GENERIC,
    };

    fn target() -> Vec<u16> {
        SESSION_TARGET.encode_utf16().chain(std::iter::once(0)).collect()
    }

    pub fn read() -> Result<Option<String>, String> {
        unsafe {
            let target = target();
            let mut credential: *mut CREDENTIALW = null_mut();
            if CredReadW(target.as_ptr(), CRED_TYPE_GENERIC, 0, &mut credential) == 0 {
                let error = GetLastError();
                if error == ERROR_NOT_FOUND { return Ok(None); }
                return Err(format!("Windows secure storage could not be read ({error})."));
            }
            let item = &*credential;
            let bytes = if item.CredentialBlobSize == 0 {
                Vec::new()
            } else {
                std::slice::from_raw_parts(item.CredentialBlob, item.CredentialBlobSize as usize).to_vec()
            };
            CredFree(credential.cast());
            String::from_utf8(bytes)
                .map(Some)
                .map_err(|_| "The saved desktop session is invalid. Please sign in again.".into())
        }
    }

    pub fn write(session: Option<String>) -> Result<(), String> {
        unsafe {
            let mut target_name = target();
            match session {
                None => {
                    if CredDeleteW(target_name.as_ptr(), CRED_TYPE_GENERIC, 0) == 0 {
                        let error = GetLastError();
                        if error != ERROR_NOT_FOUND {
                            return Err(format!("Windows secure storage could not clear the session ({error})."));
                        }
                    }
                }
                Some(value) => {
                    if value.len() > 2_400 {
                        return Err("The saved session exceeds the secure storage limit.".into());
                    }
                    let mut bytes = value.into_bytes();
                    let mut user_name: Vec<u16> = "SmartPrint".encode_utf16().chain(std::iter::once(0)).collect();
                    let mut credential: CREDENTIALW = std::mem::zeroed();
                    credential.Type = CRED_TYPE_GENERIC;
                    credential.TargetName = target_name.as_mut_ptr();
                    credential.CredentialBlobSize = bytes.len() as u32;
                    credential.CredentialBlob = bytes.as_mut_ptr();
                    credential.Persist = CRED_PERSIST_LOCAL_MACHINE;
                    credential.UserName = user_name.as_mut_ptr();
                    if CredWriteW(&credential, 0) == 0 {
                        let error = GetLastError();
                        bytes.fill(0);
                        return Err(format!("Windows secure storage could not save the session ({error})."));
                    }
                    bytes.fill(0);
                }
            }
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::agent_pipe_path;

    #[test]
    fn agent_pipe_uses_the_canonical_windows_named_pipe_namespace() {
        assert_eq!(agent_pipe_path(), r"\\.\pipe\QRtoPrint.SmartPrint.Agent.Control");
    }
}


#[cfg(windows)]
mod msix_webview2 {
    use std::process::Command;

    const WEBVIEW2_CLIENT_ID: &str = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    const ERROR_INSUFFICIENT_BUFFER: u32 = 122;
    const MB_OK: u32 = 0x0000_0000;
    const MB_ICONINFORMATION: u32 = 0x0000_0040;
    const MB_ICONERROR: u32 = 0x0000_0010;

    #[link(name = "kernel32")]
    extern "system" {
        fn GetCurrentPackageFullName(package_full_name_length: *mut u32, package_full_name: *mut u16) -> u32;
    }

    #[link(name = "user32")]
    extern "system" {
        fn MessageBoxW(hwnd: *mut std::ffi::c_void, text: *const u16, caption: *const u16, kind: u32) -> i32;
    }

    fn is_msix_packaged_process() -> bool {
        let mut length = 0u32;
        unsafe {
            GetCurrentPackageFullName(&mut length, std::ptr::null_mut()) == ERROR_INSUFFICIENT_BUFFER
        }
    }

    fn show_message(title: &str, message: &str, icon: u32) {
        let title: Vec<u16> = title.encode_utf16().chain(std::iter::once(0)).collect();
        let message: Vec<u16> = message.encode_utf16().chain(std::iter::once(0)).collect();
        unsafe {
            MessageBoxW(std::ptr::null_mut(), message.as_ptr(), title.as_ptr(), MB_OK | icon);
        }
    }

    const HKEY_LOCAL_MACHINE: isize = -2_147_483_646;
    const HKEY_CURRENT_USER: isize = -2_147_483_647;
    const KEY_QUERY_VALUE: u32 = 0x0001;
    const REG_SZ: u32 = 1;
    const ERROR_SUCCESS: i32 = 0;

    #[link(name = "advapi32")]
    extern "system" {
        #[link_name = "RegOpenKeyExW"]
        fn reg_open_key_ex_w(
            key: *mut std::ffi::c_void,
            sub_key: *const u16,
            options: u32,
            desired_access: u32,
            result: *mut *mut std::ffi::c_void,
        ) -> i32;

        #[link_name = "RegQueryValueExW"]
        fn reg_query_value_ex_w(
            key: *mut std::ffi::c_void,
            value_name: *const u16,
            reserved: *mut u32,
            value_type: *mut u32,
            data: *mut u8,
            data_size: *mut u32,
        ) -> i32;

        #[link_name = "RegCloseKey"]
        fn reg_close_key(key: *mut std::ffi::c_void) -> i32;
    }

    fn parse_registry_runtime_version(value: &str) -> bool {
        let parts: Vec<&str> = value.trim_matches('\0').trim().split('.').collect();
        parts.len() == 4
            && parts.iter().all(|part| !part.is_empty() && part.bytes().all(|byte| byte.is_ascii_digit()))
            && parts[0].parse::<u32>().map(|major| major > 0).unwrap_or(false)
    }

    fn registry_key_has_runtime(root_key: isize, sub_key: &str) -> bool {
        let sub_key: Vec<u16> = sub_key.encode_utf16().chain(std::iter::once(0)).collect();
        let value_name: Vec<u16> = "pv".encode_utf16().chain(std::iter::once(0)).collect();

        unsafe {
            let mut key = std::ptr::null_mut();
            if reg_open_key_ex_w(
                root_key as *mut std::ffi::c_void,
                sub_key.as_ptr(),
                0,
                KEY_QUERY_VALUE,
                &mut key,
            ) != ERROR_SUCCESS {
                return false;
            }

            let mut value_type = 0u32;
            let mut data_size = 0u32;
            let size_status = reg_query_value_ex_w(
                key,
                value_name.as_ptr(),
                std::ptr::null_mut(),
                &mut value_type,
                std::ptr::null_mut(),
                &mut data_size,
            );

            if size_status != ERROR_SUCCESS || value_type != REG_SZ || data_size < 2 || data_size > 1024 {
                reg_close_key(key);
                return false;
            }

            let mut data = vec![0u16; (data_size as usize + 1) / 2];
            let mut actual_size = data_size;
            let read_status = reg_query_value_ex_w(
                key,
                value_name.as_ptr(),
                std::ptr::null_mut(),
                &mut value_type,
                data.as_mut_ptr().cast::<u8>(),
                &mut actual_size,
            );
            reg_close_key(key);

            if read_status != ERROR_SUCCESS
                || value_type != REG_SZ
                || actual_size > data_size
                || actual_size % 2 != 0
            {
                return false;
            }

            let units = &data[..(actual_size as usize / 2)];
            let value_length = units.iter().position(|unit| *unit == 0).unwrap_or(units.len());
            String::from_utf16(&units[..value_length])
                .map(|version| parse_registry_runtime_version(&version))
                .unwrap_or(false)
        }
    }

    fn webview2_runtime_installed() -> bool {
        let machine_key = format!(
            r"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{}",
            WEBVIEW2_CLIENT_ID
        );
        let user_key = format!(
            r"Software\Microsoft\EdgeUpdate\Clients\{}",
            WEBVIEW2_CLIENT_ID
        );
        registry_key_has_runtime(HKEY_LOCAL_MACHINE, &machine_key)
            || registry_key_has_runtime(HKEY_CURRENT_USER, &user_key)
    }

    pub fn ensure_runtime_for_msix() -> bool {
        // The website EXE installer already installs WebView2 before launch.
        // Keep its runtime behavior unchanged; this fallback is MSIX-only.
        if !is_msix_packaged_process() || webview2_runtime_installed() {
            return true;
        }

        show_message(
            "Preparing Smart Print",
            "Smart Print is preparing the required Microsoft WebView2 Runtime. This is a one-time setup on this PC. Please wait.",
            MB_ICONINFORMATION,
        );

        let installer = match std::env::current_exe()
            .ok()
            .and_then(|path| path.parent().map(|parent| parent.join("MicrosoftEdgeWebView2RuntimeInstallerX64.exe")))
        {
            Some(path) if path.is_file() => path,
            _ => {
                show_message(
                    "Smart Print setup could not continue",
                    "The bundled Microsoft WebView2 Runtime installer is missing. Repair or reinstall Smart Print, then try again.",
                    MB_ICONERROR,
                );
                return false;
            }
        };

        let status = Command::new(&installer)
            .args(["/silent", "/install"])
            .status();

        match status {
            Ok(exit) if matches!(exit.code(), Some(0) | Some(3010)) => {}
            Ok(exit) => {
                show_message(
                    "Smart Print could not prepare WebView2",
                    &format!(
                        "Microsoft WebView2 Runtime setup returned exit code {:?}. Install the Evergreen Runtime from https://developer.microsoft.com/microsoft-edge/webview2/ and reopen Smart Print.",
                        exit.code()
                    ),
                    MB_ICONERROR,
                );
                return false;
            }
            Err(error) => {
                show_message(
                    "Smart Print could not prepare WebView2",
                    &format!(
                        "Microsoft WebView2 Runtime setup could not start: {error}. Install the Evergreen Runtime from https://developer.microsoft.com/microsoft-edge/webview2/ and reopen Smart Print."
                    ),
                    MB_ICONERROR,
                );
                return false;
            }
        }

        if !webview2_runtime_installed() {
            show_message(
                "Smart Print could not prepare WebView2",
                "Microsoft WebView2 Runtime is still unavailable. Install the Evergreen Runtime from https://developer.microsoft.com/microsoft-edge/webview2/ and reopen Smart Print.",
                MB_ICONERROR,
            );
            return false;
        }

        true
    }

    #[cfg(test)]
    mod tests {
        use super::parse_registry_runtime_version;

        #[test]
        fn accepts_a_real_webview2_runtime_version() {
            assert!(parse_registry_runtime_version("140.0.3485.54"));
        }

        #[test]
        fn rejects_empty_zero_and_malformed_webview2_runtime_versions() {
            assert!(!parse_registry_runtime_version("0.0.0.0"));
            assert!(!parse_registry_runtime_version(""));
            assert!(!parse_registry_runtime_version("not-a-version"));
            assert!(!parse_registry_runtime_version("140.0"));
        }
    }
}

fn main() {
    #[cfg(windows)]
    if !msix_webview2::ensure_runtime_for_msix() {
        std::process::exit(1);
    }

    tauri::Builder::default()
        .invoke_handler(tauri::generate_handler![
            get_local_agent_status,
            get_local_network_snapshot,
            select_local_network,
            start_local_phone_test,
            enroll_local_agent,
            read_secure_session,
            write_secure_session
        ])
        .run(tauri::generate_context!())
        .expect("error while running SmartPrint desktop");
}
