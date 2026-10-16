package com.dicaeopolis.snad

/**
 * 预共享密钥占位文件。
 *
 * 真实密钥在构建时注入，本文件会被 CI 或本地构建脚本覆盖：
 *   - CI：由 .github/workflows/build-apk.yml 的 "Inject pre-shared key" 步骤生成
 *   - 本地：由 pc-receiver/make-android-secret.py 从仓库根目录 .snad-key 生成
 *
 * 这里留空的目的是让代码在没有密钥时也能编译通过；
 * 但运行时若密钥为空，信标监听会明确报错并停止监听，不会静默降级到"不校验"。
 */
internal object SnadSecret {
    const val KEY_ID = ""
    const val PSK = ""
}
