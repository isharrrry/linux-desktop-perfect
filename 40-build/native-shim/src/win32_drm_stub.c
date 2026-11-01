// ── 【清算 / K-181】Windows Media DRM：按 AGENTS.md 第 5 条做「诚实降级」 ────────
//
// 为什么必须**导出**而不是留成 gap：
//   `AGENTS.md` 硬约束第 5 条要求「不可实现路径必须返回明确错误码并登记」，
//   第 8 条要求「DRM 按 D-001 报错降级」。
//   如果这 50 个 `DRM*` 只是"不导出"，上游一调就是
//     `EntryPointNotFoundException: Unable to find an entry point named 'DRMXxx'`
//   —— 那是**崩溃**，不是**降级**。
//   导出一个恒返回 `E_NOTIMPL`(0x80004001) 的桩，调用方才能拿到明确的错误码并走它的错误分支。
//
// Linux 上为什么真的做不了：
//   Windows Media DRM 依赖 Windows 的 **DRM 证书/密钥库/安全存储（`drmv2clt` + `WMRM`）**，
//   以及微软的授权服务器协议。Linux 上没有对应实现，也没有可移植的开源替代。
//
// 签名口径：
//   上游把这些声明为 `int`（`HRESULT`）返回、参数若干。本 shim 用 `int32_t name(void)` 定义 ——
//   **在 x86-64 SysV ABI 下，调用方多传的参数进寄存器，被调方不读就无副作用**，
//   所以这个写法能安全承接任意实参。这不是"凑合"：C 语言里空参数列表本就表示"不检查实参"。
//
// 返回值：`E_NOTIMPL` = 0x80004001（HRESULT），转成 int32_t 是负数。
#include "win32_internal.h"

#define E_NOTIMPL_HRESULT ((int32_t)0x80004001)

#define WPF_DRM_STUB(name) int32_t name(void) { return E_NOTIMPL_HRESULT; }

WPF_DRM_STUB(DRMAcquireLicense)
WPF_DRM_STUB(DRMActivate)
WPF_DRM_STUB(DRMAddRightWithUser)
WPF_DRM_STUB(DRMCloseEnvironmentHandle)
WPF_DRM_STUB(DRMCloseHandle)
WPF_DRM_STUB(DRMClosePubHandle)
WPF_DRM_STUB(DRMCloseQueryHandle)
WPF_DRM_STUB(DRMCloseSession)
WPF_DRM_STUB(DRMCreateBoundLicense)
WPF_DRM_STUB(DRMCreateClientSession)
WPF_DRM_STUB(DRMCreateEnablingBitsDecryptor)
WPF_DRM_STUB(DRMCreateEnablingBitsEncryptor)
WPF_DRM_STUB(DRMCreateIssuanceLicense)
WPF_DRM_STUB(DRMCreateLicenseStorageSession)
WPF_DRM_STUB(DRMCreateRight)
WPF_DRM_STUB(DRMCreateUser)
WPF_DRM_STUB(DRMDeconstructCertificateChain)
WPF_DRM_STUB(DRMDecrypt)
WPF_DRM_STUB(DRMDeleteLicense)
WPF_DRM_STUB(DRMEncrypt)
WPF_DRM_STUB(DRMEnumerateLicense)
WPF_DRM_STUB(DRMGetApplicationSpecificData)
WPF_DRM_STUB(DRMGetBoundLicenseAttribute)
WPF_DRM_STUB(DRMGetBoundLicenseObject)
WPF_DRM_STUB(DRMGetInfo)
WPF_DRM_STUB(DRMGetIntervalTime)
WPF_DRM_STUB(DRMGetIssuanceLicenseInfo)
WPF_DRM_STUB(DRMGetIssuanceLicenseTemplate)
WPF_DRM_STUB(DRMGetNameAndDescription)
WPF_DRM_STUB(DRMGetOwnerLicense)
WPF_DRM_STUB(DRMGetRevocationPoint)
WPF_DRM_STUB(DRMGetRightInfo)
WPF_DRM_STUB(DRMGetSecurityProvider)
WPF_DRM_STUB(DRMGetServiceLocation)
WPF_DRM_STUB(DRMGetSignedIssuanceLicense)
WPF_DRM_STUB(DRMGetUnboundLicenseAttribute)
WPF_DRM_STUB(DRMGetUnboundLicenseObject)
WPF_DRM_STUB(DRMGetUnboundLicenseObjectCount)
WPF_DRM_STUB(DRMGetUserInfo)
WPF_DRM_STUB(DRMGetUserRights)
WPF_DRM_STUB(DRMGetUsers)
WPF_DRM_STUB(DRMInitEnvironment)
WPF_DRM_STUB(DRMIsActivated)
WPF_DRM_STUB(DRMParseUnboundLicense)
WPF_DRM_STUB(DRMRegisterContent)
WPF_DRM_STUB(DRMSetApplicationSpecificData)
WPF_DRM_STUB(DRMSetIntervalTime)
WPF_DRM_STUB(DRMSetMetaData)
WPF_DRM_STUB(DRMSetNameAndDescription)
WPF_DRM_STUB(DRMSetRevocationPoint)
