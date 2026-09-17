# 学生档案子表端到端探针（真实 HTTP）
# 目的：路由是 ABP 约定推出来的，进程内测试发现不了"动词/路径写错"，
#       而且子表接口**是否真的做了数据范围校验**只有打真接口才能确认。
$ErrorActionPreference = "Continue"
$u8 = [System.Text.UTF8Encoding]::new($false)
$base = "http://127.0.0.1:44300"
$log = "D:\StudentManagement\Logs\probe"

function RJ($p) { [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($p)) | ConvertFrom-Json }

# 返回 @{ status; text }
function Call($method, $path, $jsonBody) {
    $body = Join-Path $log "body.json"
    $resp = Join-Path $log "resp.json"
    if ($null -ne $jsonBody) {
        [System.IO.File]::WriteAllText($body, ($jsonBody | ConvertTo-Json -Depth 8 -Compress), $u8)
    }
    $curlArgs = @("-sS", "--max-time", "90", "-o", $resp, "-w", "%{http_code}", "-X", $method, "$base$path", "-H", "Authorization: Bearer $script:token")
    if ($null -ne $jsonBody) { $curlArgs += @("-H", "Content-Type: application/json; charset=utf-8", "--data-binary", "@$body") }
    $status = & curl.exe @curlArgs 2>&1
    $text = if (Test-Path $resp) { [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($resp)) } else { "" }
    return @{ status = "$status".Trim(); text = $text }
}

function Check($name, $condition, $detail) {
    if ($condition) { Write-Output "  [通过] $name" }
    else { Write-Output "  [失败] $name  -> $detail"; $script:failed++ }
}

New-Item -ItemType Directory -Force -Path $log | Out-Null
$script:failed = 0
$script:token = ""

# ---- 0) 等待就绪并登录 ----
foreach ($i in 1..25) {
    Start-Sleep -Seconds 4
    try { $r = & curl.exe -sS --max-time 5 -o NUL -w "%{http_code}" "$base/swagger/v1/swagger.json" 2>$null } catch { $r = "000" }
    if ($r -eq "200") { break }
}
$tokenResp = Call "POST" "/connect/token" $null
$form = "grant_type=password&username=admin&password=1q2w3E%2A&client_id=Xjgl_App&scope=Xjgl%20offline_access"
$status = & curl.exe -sS --max-time 90 -o (Join-Path $log "token.json") -w "%{http_code}" -X POST "$base/connect/token" -H "Content-Type: application/x-www-form-urlencoded" --data $form 2>&1
$script:token = (RJ (Join-Path $log "token.json")).access_token
Write-Output "登录 HTTP $status，令牌长度 $($script:token.Length)"
if (-not $script:token) { Write-Output "登录失败，终止"; exit 1 }

# ---- 1) 找学校 + 建一名测试学生 ----
$schools = (Call "GET" "/api/app/org?Keyword=%E6%B5%8B%E8%AF%95%E7%AC%AC%E4%B8%80%E5%B0%8F%E5%AD%A6&onlyEnabled=false" $null).text | ConvertFrom-Json
$school = $schools[0]
Write-Output "目标学校：$($school.name) / $($school.code)"

function NewIdCard() {
    $p = "4101022016{0:D2}{1:D2}{2:D3}" -f (Get-Random -Minimum 1 -Maximum 13), (Get-Random -Minimum 1 -Maximum 28), (Get-Random -Minimum 100 -Maximum 999)
    $w = @(7,9,10,5,8,4,2,1,6,3,7,9,10,5,8,4,2); $c = "10X98765432"; $s = 0
    for ($k = 0; $k -lt 17; $k++) { $s += ([int]::Parse($p[$k])) * $w[$k] }
    return $p + $c[$s % 11]
}

$studentIdCard = NewIdCard
$created = Call "POST" "/api/app/student" @{
    name = "子表探针学生"; genderCode = "1"; birthDate = "2016-05-06T00:00:00"
    idCardTypeCode = "1"; idCardNumber = $studentIdCard; schoolOrgId = $school.id
    academicYear = "2025-2026"
}
Check "建测试学生 HTTP 200" ($created.status -eq "200") $created.text
$studentId = ($created.text | ConvertFrom-Json).id
Write-Output "学生 Id = $studentId"

# ---- 2) 监护人：空列表 / 新增（掩码）/ 第一监护人唯一 / 留空不修改 ----
$list0 = Call "GET" "/api/app/student-profile/guardians/$studentId" $null
Check "GET guardians HTTP 200" ($list0.status -eq "200") "$($list0.status) $($list0.text)"
Check "初始监护人列表为空" (($list0.text | ConvertFrom-Json).Count -eq 0) $list0.text

$guardianIdCard = NewIdCard
$g1 = Call "POST" "/api/app/student-profile/guardian/$studentId" @{
    name = "探针家长甲"; relationCode = "1"; idCardNumber = $guardianIdCard
    phoneNumber = "13800000001"; workUnit = "探针单位"; address = "探针地址"
}
$g1body = $g1.text | ConvertFrom-Json
Check "新增监护人 HTTP 200" ($g1.status -eq "200") "$($g1.status) $($g1.text)"
Check "返回身份证仅为掩码（前6后4）" ($g1body.maskedIdCard -match '^\d{6}\*{8}\d{4}$') $g1body.maskedIdCard
Check "响应体不含身份证明文" (-not ($g1.text -match $guardianIdCard)) "响应里出现了明文"
Check "关系码翻译成中文" ($g1body.relationName -eq "父亲") $g1body.relationName
Check "序号由服务端分配（=1）" ($g1body.seq -eq 1) $g1body.seq

$g2 = Call "POST" "/api/app/student-profile/guardian/$studentId" @{
    name = "探针家长乙"; relationCode = "2"; phoneNumber = "13800000002"; isPrimary = $true
}
Check "新增第二监护人 HTTP 200" ($g2.status -eq "200") $g2.text

$list2 = (Call "GET" "/api/app/student-profile/guardians/$studentId" $null).text | ConvertFrom-Json
$jia = $list2 | Where-Object { $_.name -eq "探针家长甲" }
$yi = $list2 | Where-Object { $_.name -eq "探针家长乙" }
Check "第一监护人唯一（甲被自动取消）" (($jia.isPrimary -eq $false) -and ($yi.isPrimary -eq $true)) ($list2 | ConvertTo-Json -Compress)

$upd = Call "PUT" "/api/app/student-profile/$($jia.id)/guardian" @{
    name = "探针家长甲"; relationCode = "1"; phoneNumber = "13900000009"
}
$updBody = $upd.text | ConvertFrom-Json
Check "修改监护人 HTTP 200" ($upd.status -eq "200") "$($upd.status) $($upd.text)"
Check "未回传身份证号时保留原号（留空=不修改）" ($updBody.hasIdCard -eq $true -and $updBody.maskedIdCard -match '\*{8}') $upd.text
Check "修改后的电话已生效" ($updBody.phoneNumber -eq "13900000009") $updBody.phoneNumber

# ---- 3) 体检：一学年一条 ----
$p1 = Call "POST" "/api/app/student-profile/physical/$studentId" @{
    academicYear = "2025-2026"; checkDate = "2025-10-15T00:00:00"
    height = 132.5; weight = 28.4; leftVision = 5.0; rightVision = 4.9; healthCode = "1"
    doctorAdvice = "注意用眼卫生"
}
Check "新增体检 HTTP 200" ($p1.status -eq "200") "$($p1.status) $($p1.text)"
Check "健康码翻译成中文" (($p1.text | ConvertFrom-Json).healthName -eq "健康") $p1.text

$p2 = Call "POST" "/api/app/student-profile/physical/$studentId" @{ academicYear = "2025-2026"; height = 133 }
# 注意：ABP 把业务异常（BusinessException）映射成 **HTTP 403**，而不是 400/409 ——
# 与"没有权限"同码，只能靠 error.code（这里是 Xjgl:00004 数据校验未通过）区分。
# 探针第一版断言 400，结果被自己的错误认知绊了一下：**接口是对的，断言是错的**。
$p2code = try { ($p2.text | ConvertFrom-Json).error.code } catch { "" }
Check "同一学年重复录入被拒（403 + Xjgl:00004）" (($p2.status -eq "403") -and ($p2code -eq "Xjgl:00004")) "$($p2.status) $($p2.text)"
Check "拒绝原因可读" ($p2.text -match "已存在") $p2.text

$p3 = Call "POST" "/api/app/student-profile/physical/$studentId" @{ academicYear = "2024-2025"; height = 128 }
Check "下一学年可正常录入" ($p3.status -eq "200") "$($p3.status) $($p3.text)"

# ---- 4) 其余四张子表冒烟（读写各一次）----
$smoke = @(
    @{ name = "简历"; path = "resume"; list = "resumes"; dto = @{ schoolName = "探针小学"; startDate = "2019-09-01T00:00:00"; post = "班长" } },
    @{ name = "奖励"; path = "reward"; list = "rewards"; dto = @{ rewardDate = "2025-06-01T00:00:00"; rewardName = "三好学生"; rewardTypeCode = "1"; rewardLevelCode = "1" } },
    @{ name = "处分"; path = "punishment"; list = "punishments"; dto = @{ punishDate = "2024-11-01T00:00:00"; punishName = "警告"; punishTypeCode = "1"; isRevoked = $true; revokeReason = "申诉成功" } },
    @{ name = "资助"; path = "subsidy"; list = "subsidies"; dto = @{ academicYear = "2025-2026"; subsidyName = "寄宿生生活补助"; subsidyTypeCode = "3"; amount = 625.5 } }
)
foreach ($s in $smoke) {
    $c = Call "POST" "/api/app/student-profile/$($s.path)/$studentId" $s.dto
    $l = Call "GET" "/api/app/student-profile/$($s.list)/$studentId" $null
    $count = try { ($l.text | ConvertFrom-Json).Count } catch { -1 }
    Check "$($s.name)：新增 200 且列表能查到" (($c.status -eq "200") -and ($l.status -eq "200") -and ($count -ge 1)) "新增=$($c.status) 列表=$($l.status) 条数=$count $($c.text)"
}

$punish = (Call "GET" "/api/app/student-profile/punishments/$studentId" $null).text | ConvertFrom-Json
Check "处分撤销后仍保留记录（不删行）" (($punish.Count -eq 1) -and ($punish[0].isRevoked -eq $true) -and ($punish[0].revokeDate)) ($punish | ConvertTo-Json -Compress)
Check "处分类型码翻译成中文" ($punish[0].punishTypeName -eq "警告") $punish[0].punishTypeName

$subsidy = (Call "GET" "/api/app/student-profile/subsidies/$studentId" $null).text | ConvertFrom-Json
Check "资助金额按 decimal 原样返回" ($subsidy[0].amount -eq 625.5) $subsidy[0].amount

# ---- 5) 数据范围：不存在的学生必须 404（证明守卫真的加载了主档）----
$ghost = Call "GET" "/api/app/student-profile/guardians/00000000-0000-0000-0000-0000000000ff" $null
Check "不存在的学生返回 404（守卫生效）" ($ghost.status -eq "404") "$($ghost.status) $($ghost.text)"

# ---- 6) 清理 ----
Write-Output "--- 清理 ---"
foreach ($g in $list2) {
    $d = Call "DELETE" "/api/app/student-profile/$($g.id)/guardian" $null
    Write-Output "  删除监护人 $($g.name) -> $($d.status)"
}
foreach ($p in ((Call "GET" "/api/app/student-profile/physicals/$studentId" $null).text | ConvertFrom-Json)) {
    $d = Call "DELETE" "/api/app/student-profile/$($p.id)/physical" $null
    Write-Output "  删除体检 $($p.academicYear) -> $($d.status)"
}
foreach ($s in $smoke) {
    foreach ($row in ((Call "GET" "/api/app/student-profile/$($s.list)/$studentId" $null).text | ConvertFrom-Json)) {
        $d = Call "DELETE" "/api/app/student-profile/$($row.id)/$($s.path)" $null
        Write-Output "  删除$($s.name) -> $($d.status)"
    }
}
$delStudent = Call "DELETE" "/api/app/student/$studentId" $null
Write-Output "  删除学生 -> $($delStudent.status)"

$after = ((Call "GET" "/api/app/student?SchoolOrgId=$($school.id)&Keyword=%E5%AD%90%E8%A1%A8%E6%8E%A2%E9%92%88&MaxResultCount=20" $null).text | ConvertFrom-Json)
Check "清理完成（列表剩余 0）" ($after.totalCount -eq 0) "剩余 $($after.totalCount)"

Write-Output "`n============================================"
if ($script:failed -eq 0) { Write-Output "全部断言通过" } else { Write-Output "失败断言数：$($script:failed)" }
exit $script:failed
