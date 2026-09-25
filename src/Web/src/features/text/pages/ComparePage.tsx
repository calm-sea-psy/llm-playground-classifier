import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { MultiDropzone } from '../../../shared/components/MultiDropzone'
import {
  applyCombo,
  createExperiment,
  deleteExperiment,
  getExperiment,
  getSettings,
  getSettingsOptions,
  listExperiments,
  sameSettings,
  type ExperimentDetail,
  type ExperimentSummary,
  type PipelineSettings,
  type SettingsDto,
  type SettingsOptions,
} from '../api'
import { ComboEditor } from '../components/ComboEditor'
import { SystemSpecs } from '../../../shared/components/SystemSpecs'

const ACCEPT = '.png,.jpg,.jpeg,.bmp,.tif,.tiff,.webp'
const MAX_FILES = 50
const MAX_COMBOS = 4
/** 건당 대략적인 처리 시간 (5·6단계 측정 중앙값 약 15초) */
const SEC_PER_JOB = 15

/**
 * 모델 비교 (todo 4번): 문서 N장 × 설정 조합 M개로 실험 ➔ 저장 ➔ 조합별 점수·순위 ➔ 1위를 추천,
 * [기본으로 적용]을 누르면 문서 처리 기본 설정이 바뀐다
 */
export function ComparePage() {
  const navigate = useNavigate()
  const [options, setOptions] = useState<SettingsOptions | null>(null)
  const [current, setCurrent] = useState<SettingsDto | null>(null)
  const [experiments, setExperiments] = useState<ExperimentSummary[]>([])
  const [latest, setLatest] = useState<ExperimentDetail | null>(null)
  const [error, setError] = useState<string | null>(null)

  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [files, setFiles] = useState<File[]>([])
  const [combos, setCombos] = useState<PipelineSettings[]>([])
  const [submitting, setSubmitting] = useState(false)
  const [skipped, setSkipped] = useState<{ id: string; name: string } | null>(null)

  const reload = useCallback(async () => {
    const [s, list] = await Promise.all([getSettings(), listExperiments()])
    setCurrent(s)
    setExperiments(list)
    // 추천은 "현재 기본 조합이 함께 들어 있는" 가장 최근 실험에서만 (기본이 없는 실험의 1위는 기본보다 나은지 알 수 없음)
    let found: Awaited<ReturnType<typeof getExperiment>> | null = null
    let skipped: { id: string; name: string } | null = null
    for (const e of list.filter((x) => x.status === 'Completed' && x.recommended)) {
      const d = await getExperiment(e.id)
      if (d.combos.some((x) => sameSettings(s.settings, x.settings))) {
        found = d
        break
      }
      skipped ??= { id: e.id, name: e.name }
    }
    setLatest(found)
    setSkipped(skipped)
    return s
  }, [])

  useEffect(() => {
    getSettingsOptions()
      .then(async (o) => {
        const s = await reload()
        setOptions(o)
        // 기본 조합 2개: 현재 기본 설정 + 다른 모델
        const other = o.models.find((m) => m !== s.settings.model) ?? s.settings.model
        setCombos([s.settings, { ...s.settings, model: other }])
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)))
  }, [reload])

  // 진행 중인 실험이 있으면 목록을 주기적으로 갱신
  const running = experiments.some((e) => e.status === 'Running')
  useEffect(() => {
    if (!running) return
    const timer = setInterval(() => reload().catch(() => {}), 5000)
    return () => clearInterval(timer)
  }, [running, reload])

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setSubmitting(true)
    setError(null)
    try {
      const created = await createExperiment(name.trim(), files, combos, description.trim())
      navigate(`/text/experiments/${created.id}`)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
      setSubmitting(false)
    }
  }

  const apply = async (experimentId: string, index: number) => {
    setCurrent(await applyCombo(experimentId, index))
  }

  const remove = async (id: string) => {
    if (!confirm('이 실험 기록을 삭제할까요? (처리된 작업은 문서 처리 기록에 남습니다)')) return
    try {
      await deleteExperiment(id)
      await reload()
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  const recommended = latest?.recommendedIndex != null ? latest.combos[latest.recommendedIndex] : null
  const baseline = current ? latest?.combos.find((x) => sameSettings(current.settings, x.settings)) : undefined
  const jobs = files.length * combos.length

  return (
    <>
      <SystemSpecs />

      <section className="card defaults">
        <div className="defaults-row">
          <div>
            <span className="spec-label">문서 처리 기본 설정</span>
            <strong>{current?.summary ?? '…'}</strong>
            {current?.note && <span className="muted small">근거: {current.note}</span>}
          </div>
          {recommended && latest && (
            <div className="recommend">
              <span className="spec-label">
                추천 · 현재 기본이 포함된 최근 실험 <Link to={`/text/experiments/${latest.id}`}>{latest.name}</Link> 1위 (점수{' '}
                {recommended.score?.toFixed(3)})
              </span>
              <strong>{recommended.summary}</strong>
              {baseline && baseline.index === recommended.index ? (
                <span className="chip chip-ok">현재 기본이 1위</span>
              ) : (
                <>
                  {baseline?.score != null && recommended.score != null && (
                    <span className="muted small">
                      같은 실험에서 현재 기본 {baseline.score.toFixed(3)} ➔ {recommended.score.toFixed(3)} (+
                      {(recommended.score - baseline.score).toFixed(3)})
                    </span>
                  )}
                  <button className="primary small-button" onClick={() => apply(latest.id, recommended.index)}>
                    기본으로 적용
                  </button>
                </>
              )}
            </div>
          )}
          {skipped && (
            <span className="muted small recommend-note">
              더 최근 실험 <Link to={`/text/experiments/${skipped.id}`}>{skipped.name}</Link> 에는 현재 기본 조합이 없어 추천 비교에서 뺐습니다.
            </span>
          )}
        </div>
      </section>

      <form className="card upload-card" onSubmit={submit}>
        <div>
          <h2>새 실험</h2>
          <p className="muted small">
            같은 문서들을 설정 조합별로 처리해 필드 정확도(정답 라벨이 있을 때)·검증 통과율·처리 시간을 비교합니다. 결과는 저장되어 아래
            실험 기록에서 다시 볼 수 있습니다.
          </p>
        </div>
        <label className="small name-field">
          실험 이름
          <input value={name} onChange={(e) => setName(e.target.value)} placeholder="예: 영수증 20장 모델·폴백 비교" maxLength={200} />
        </label>
        <label className="small name-field">
          실험 설명 (선택)
          <textarea
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            rows={2}
            maxLength={2000}
            placeholder="문서를 어떻게 골랐는지, 무엇을 확인하려는지 (예: 신뢰도 0.9 미만 문서만 골라 폴백 기준 비교)"
          />
        </label>
        <MultiDropzone accept={ACCEPT} files={files} onFiles={setFiles} disabled={submitting} max={MAX_FILES} />
        {options && (
          <div className="combos">
            {combos.map((c, i) => (
              <ComboEditor
                key={i}
                index={i}
                value={c}
                options={options}
                isDefault={!!current && sameSettings(current.settings, c)}
                disabled={submitting}
                onChange={(v) => setCombos(combos.map((x, j) => (j === i ? v : x)))}
                onRemove={combos.length > 1 ? () => setCombos(combos.filter((_, j) => j !== i)) : undefined}
              />
            ))}
            {combos.length < MAX_COMBOS && (
              <button type="button" className="add-combo" onClick={() => setCombos([...combos, combos[combos.length - 1]])}>
                + 조합 추가
              </button>
            )}
          </div>
        )}
        <div className="upload-actions">
          <span className="muted small">
            {jobs ? `작업 ${jobs}건 · 예상 약 ${Math.max(1, Math.round((jobs * SEC_PER_JOB) / 60))}분 (GPU 1개로 한 건씩 처리)` : '문서를 선택하세요'}
          </span>
          <button type="submit" className="primary" disabled={!files.length || !combos.length || submitting}>
            {submitting ? '업로드 중…' : '실험 시작'}
          </button>
        </div>
        {error && <p className="text-bad small">{error}</p>}
      </form>

      <section className="card">
        <h3>실험 기록</h3>
        {experiments.length === 0 ? (
          <p className="muted small">아직 실험이 없습니다.</p>
        ) : (
          <div className="table-scroll">
            <table className="jobs">
              <thead>
                <tr>
                  <th>이름</th>
                  <th>규모</th>
                  <th>상태</th>
                  <th className="hide-sm">추천 조합 (1위)</th>
                  <th className="hide-sm">시각</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {experiments.map((e) => (
                  <tr key={e.id}>
                    <td>
                      <Link to={`/text/experiments/${e.id}`}>{e.name}</Link>
                    </td>
                    <td className="small tabular">
                      {e.docCount}장 × {e.comboCount}조합{e.labeled && <span className="chip">정답 있음</span>}
                    </td>
                    <td className="small">
                      {e.status === 'Completed' ? (
                        <span className="badge badge-ok">완료</span>
                      ) : (
                        <span className="badge badge-run">
                          {e.done}/{e.total}
                        </span>
                      )}
                    </td>
                    <td className="small hide-sm">
                      {e.recommended ? `${e.recommended} · ${e.bestScore?.toFixed(3)}` : <span className="muted">—</span>}
                    </td>
                    <td className="muted small tabular hide-sm">{new Date(e.createdAt).toLocaleString('ko-KR')}</td>
                    <td>
                      {e.status === 'Completed' && (
                        <button className="link-button small" onClick={() => remove(e.id)}>
                          삭제
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </>
  )
}
