# 上传到 GitHub 的步骤

仓库已经在 `hdu-booking\` 里初始化好了（分支 `main`，已有 1 个提交），**只差配置远程地址并推送**。

## 一、先在 GitHub 上建一个空仓库

打开 https://github.com/new ，填仓库名（例如 `hdu-court-booker`），
**不要**勾选 "Add a README file" / ".gitignore" / "license"（本地已经有了，勾了会冲突）。

建好后复制仓库地址，形如：
- HTTPS：`https://github.com/<你的用户名>/hdu-court-booker.git`
- SSH：`git@github.com:<你的用户名>/hdu-court-booker.git`

## 二、推送（把下面的地址换成你自己的）

```powershell
cd C:\Users\10386\Documents\deepseek-harness\default-workspace\hdu-booking

# 1) 配置提交身份（如果这是你第一次用 git，建议设成你 GitHub 的邮箱）
git config user.name  "你的名字"
git config user.email "你的邮箱@example.com"

# 2) 关联远程仓库
git remote add origin https://github.com/<你的用户名>/hdu-court-booker.git

# 3) 推送
git push -u origin main
```

首次推送会弹出浏览器/窗口让你登录 GitHub 授权，按提示完成即可。

> 若第 1 步已经在全局配过（`git config --global user.name`），可以跳过。
> 如果推送时报 `remote origin already exists`，说明已经配过远程，改用：
> `git remote set-url origin <新地址>`

## 三、推送后建议做的事

1. **把 `publish-lite/` 里的 exe 作为 Release 附件**（可选，更规范）：
   GitHub 仓库页 → Releases → Draft a new release → Tag 填 `v1.0.0` →
   把 `publish-lite\HduCourtBooker.exe` 和 `HduCourtBooker.SelfTest.exe` 拖进去发布。
   仓库里保留源码即可，二进制放 Release 能避免仓库变大。

2. **确认仓库里没有凭据**（正常情况已经被 .gitignore 排除）：

   ```powershell
   git ls-files | Select-String -Pattern 'data/|\.log$|recon/'
   ```

   应当**没有任何输出**。若不幸有输出，说明误提交了凭据，需要立刻删除并更换 token。

## 四、安全提醒

`data\config.json` 里的 `TokenProtected` 是**明文 token**（等同你的登录凭据）。
本仓库的 `.gitignore` 已经把 `data/`、`*.log`、`crash-*.log`、`recon/` 全部排除，
但请你在以后每次 `git add` 前养成习惯看一眼：

```powershell
git status --short
```

只要看到 `config.json`、`*.log`、`data/` 之类的文件，就不要提交。
